[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Root,

    [string] $AcceptanceHostPath = (
        ".\tools\MediaFileRenamer.AcceptanceHost\bin\Release\net10.0-windows\" +
        "MediaFileRenamer.AcceptanceHost.exe"),

    [ValidateRange(8, 4096)]
    [int] $InterruptionFileSizeMB = 64
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$knownTimestamp = [DateTime]::SpecifyKind(
    [DateTime]::ParseExact(
        "2020-02-03T04:05:06",
        "yyyy-MM-ddTHH:mm:ss",
        [Globalization.CultureInfo]::InvariantCulture),
    [DateTimeKind]::Utc)

function Get-SafeBasePath {
    param([Parameter(Mandatory)][string] $Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ($expanded -notmatch '^[A-Za-z]:[\\/]') {
        throw "Root must be an absolute local path: '$Path'."
    }

    $full = [IO.Path]::GetFullPath($expanded)
    $pathRoot = [IO.Path]::GetPathRoot($full)
    if ($full.TrimEnd('\', '/') -eq $pathRoot.TrimEnd('\', '/')) {
        throw "A drive root is too broad for acceptance evidence: '$full'."
    }

    return $full
}

function Assert-Acceptance {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function New-FixtureFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [int] $SizeMB = 0,
        [string] $Content = "Media File Renamer acceptance fixture"
    )

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) |
        Out-Null
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    try {
        if ($SizeMB -gt 0) {
            $stream.SetLength([int64]$SizeMB * 1MB)
        } else {
            $bytes = [Text.Encoding]::UTF8.GetBytes($Content)
            $stream.Write($bytes, 0, $bytes.Length)
        }
    } finally {
        $stream.Dispose()
    }

    [IO.File]::SetCreationTimeUtc($Path, $knownTimestamp)
    [IO.File]::SetLastWriteTimeUtc($Path, $knownTimestamp)
}

function Get-FileEvidence {
    param([Parameter(Mandatory)][string] $Path)

    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $item.FullName
        length = $item.Length
        creationTimeUtc = $item.CreationTimeUtc.ToString("O")
        lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("O")
        attributes = $item.Attributes.ToString()
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
            Hash.ToLowerInvariant()
    }
}

function Assert-MetadataEqual {
    param(
        [Parameter(Mandatory)] $Expected,
        [Parameter(Mandatory)] $Actual,
        [Parameter(Mandatory)][string] $Context
    )

    Assert-Acceptance ($Expected.length -eq $Actual.length) `
        "$Context length changed."
    Assert-Acceptance ($Expected.sha256 -eq $Actual.sha256) `
        "$Context content hash changed."
    Assert-Acceptance (
        $Expected.creationTimeUtc -eq $Actual.creationTimeUtc) `
        "$Context creation timestamp changed."
    Assert-Acceptance (
        $Expected.lastWriteTimeUtc -eq $Actual.lastWriteTimeUtc) `
        "$Context last-write timestamp changed."
    Assert-Acceptance ($Expected.attributes -eq $Actual.attributes) `
        "$Context safe Windows attributes changed."
}

function Invoke-AcceptanceHost {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [int[]] $ExpectedExitCodes = @(0)
    )

    $lines = & $script:hostPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($ExpectedExitCodes -notcontains $exitCode) {
        throw "Acceptance host exited $exitCode. Output: $($lines -join ' ')"
    }

    return ($lines -join [Environment]::NewLine) | ConvertFrom-Json
}

function Wait-ForPath {
    param(
        [Parameter(Mandatory)][string] $Path,
        [int] $Seconds = 30
    )

    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ((Get-Date) -ge $deadline) {
            throw "Timed out waiting for '$Path'."
        }

        Start-Sleep -Milliseconds 100
    }
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][scriptblock] $Body
    )

    $started = [DateTime]::UtcNow
    try {
        $evidence = & $Body
        $script:results.Add([ordered]@{
            id = $Id
            result = "Pass"
            startedAtUtc = $started.ToString("O")
            completedAtUtc = [DateTime]::UtcNow.ToString("O")
            evidence = $evidence
        })
    } catch {
        $script:results.Add([ordered]@{
            id = $Id
            result = "Fail"
            startedAtUtc = $started.ToString("O")
            completedAtUtc = [DateTime]::UtcNow.ToString("O")
            error = $_.Exception.Message
        })
    }
}

$base = Get-SafeBasePath -Path $Root
$script:hostPath = [IO.Path]::GetFullPath($AcceptanceHostPath)
if (-not (Test-Path -LiteralPath $script:hostPath -PathType Leaf)) {
    throw "Build the acceptance host first: '$script:hostPath'."
}

$runId = "MFR-FileAcceptance-{0}-{1}" -f `
    (Get-Date -Format "yyyyMMdd-HHmmss"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 10))
$runRoot = Join-Path $base $runId
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$nonce = [Guid]::NewGuid().ToString("N")
$markerPath = Join-Path $runRoot ".mfr-acceptance-$nonce"
[IO.File]::WriteAllText(
    $markerPath,
    $nonce,
    [Text.UTF8Encoding]::new($false))

$script:results = [Collections.Generic.List[object]]::new()

Invoke-Scenario "COPY-01/COPY-02" {
    $root = Join-Path $runRoot "copy"
    $source = Join-Path $root "source\Sample.Movie.2026.mkv"
    $companion = Join-Path $root "source\Sample.Movie.2026.en.srt"
    $destination = Join-Path $root "destination\Sample Movie (2026).mkv"
    $destinationCompanion = [IO.Path]::ChangeExtension(
        $destination,
        ".en.srt")
    $journal = Join-Path $root "journals"
    New-FixtureFile -Path $source -Content "copy primary $nonce"
    New-FixtureFile -Path $companion -Content "copy subtitle $nonce"
    [IO.File]::SetAttributes(
        $source,
        [IO.FileAttributes]::Archive -bor [IO.FileAttributes]::ReadOnly)
    $sourceBefore = Get-FileEvidence $source
    $companionBefore = Get-FileEvidence $companion

    $transfer = Invoke-AcceptanceHost @(
        "transfer", "--operation", "Copy",
        "--source", $source,
        "--destination", $destination,
        "--companion", $companion,
        "--journal", $journal)
    Assert-Acceptance $transfer.success "Copy transfer failed."
    Assert-Acceptance (Test-Path -LiteralPath $source) `
        "Copy removed its source."
    Assert-MetadataEqual $sourceBefore (Get-FileEvidence $destination) `
        "Copied primary"
    Assert-MetadataEqual $companionBefore (
        Get-FileEvidence $destinationCompanion) "Copied companion"

    $undo = Invoke-AcceptanceHost @("undo", "--journal", $journal)
    Assert-Acceptance $undo.success "Copy Undo failed."
    Assert-Acceptance (-not (Test-Path -LiteralPath $destination)) `
        "Copy Undo left the primary destination."
    Assert-Acceptance (
        -not (Test-Path -LiteralPath $destinationCompanion)) `
        "Copy Undo left the companion destination."

    return [ordered]@{
        transfer = $transfer
        undo = $undo
        source = Get-FileEvidence $source
        companion = Get-FileEvidence $companion
    }
}

Invoke-Scenario "MOVE-01/MOVE-02" {
    $root = Join-Path $runRoot "move"
    $source = Join-Path $root "source\Sample.Episode.S01E01.mkv"
    $companion = Join-Path $root "source\Sample.Episode.S01E01.nfo"
    $destination = Join-Path $root "destination\Sample Episode - S01E01.mkv"
    $destinationCompanion = [IO.Path]::ChangeExtension($destination, ".nfo")
    $journal = Join-Path $root "journals"
    New-FixtureFile -Path $source -Content "move primary $nonce"
    New-FixtureFile -Path $companion -Content "move nfo $nonce"
    $sourceBefore = Get-FileEvidence $source
    $companionBefore = Get-FileEvidence $companion

    $transfer = Invoke-AcceptanceHost @(
        "transfer", "--operation", "Move",
        "--source", $source,
        "--destination", $destination,
        "--companion", $companion,
        "--journal", $journal)
    Assert-Acceptance $transfer.success "Move transfer failed."
    Assert-Acceptance (-not (Test-Path -LiteralPath $source)) `
        "Move retained its source."
    Assert-MetadataEqual $sourceBefore (Get-FileEvidence $destination) `
        "Moved primary"
    Assert-MetadataEqual $companionBefore (
        Get-FileEvidence $destinationCompanion) "Moved companion"

    $undo = Invoke-AcceptanceHost @("undo", "--journal", $journal)
    Assert-Acceptance $undo.success "Move Undo failed."
    Assert-MetadataEqual $sourceBefore (Get-FileEvidence $source) `
        "Restored primary"
    Assert-MetadataEqual $companionBefore (Get-FileEvidence $companion) `
        "Restored companion"

    return [ordered]@{
        transfer = $transfer
        undo = $undo
        source = Get-FileEvidence $source
        companion = Get-FileEvidence $companion
    }
}

Invoke-Scenario "COLL-01" {
    $root = Join-Path $runRoot "collision"
    $source = Join-Path $root "source\Collision.mkv"
    $destination = Join-Path $root "destination\Collision.mkv"
    $journal = Join-Path $root "journals"
    New-FixtureFile -Path $source -Content "collision source $nonce"
    New-FixtureFile -Path $destination -Content "sentinel $nonce"
    $sourceBefore = Get-FileEvidence $source
    $sentinelBefore = Get-FileEvidence $destination

    $transfer = Invoke-AcceptanceHost `
        -Arguments @(
            "transfer", "--operation", "Copy",
            "--source", $source,
            "--destination", $destination,
            "--journal", $journal) `
        -ExpectedExitCodes @(10)
    Assert-Acceptance (-not $transfer.success) `
        "Collision unexpectedly succeeded."
    Assert-MetadataEqual $sourceBefore (Get-FileEvidence $source) `
        "Collision source"
    Assert-MetadataEqual $sentinelBefore (Get-FileEvidence $destination) `
        "Collision sentinel"

    return [ordered]@{
        transfer = $transfer
        source = Get-FileEvidence $source
        sentinel = Get-FileEvidence $destination
    }
}

Invoke-Scenario "CANCEL-01" {
    $root = Join-Path $runRoot "cancel"
    $source = Join-Path $root "source\Cancel.mkv"
    $destination = Join-Path $root "destination\Cancel.mkv"
    $journal = Join-Path $root "journals"
    $paused = Join-Path $root "paused.json"
    New-FixtureFile -Path $source -SizeMB $InterruptionFileSizeMB

    $transfer = Invoke-AcceptanceHost `
        -Arguments @(
            "transfer", "--operation", "Copy",
            "--source", $source,
            "--destination", $destination,
            "--journal", $journal,
            "--pause-at-bytes", "1048576",
            "--paused-marker", $paused,
            "--cancel-when-paused") `
        -ExpectedExitCodes @(10)
    Assert-Acceptance (-not $transfer.success) `
        "Cancellation unexpectedly succeeded."
    Assert-Acceptance $transfer.rolledBack `
        "Cancellation did not report a complete rollback."
    Assert-Acceptance (Test-Path -LiteralPath $source) `
        "Cancellation removed its source."
    Assert-Acceptance (-not (Test-Path -LiteralPath $destination)) `
        "Cancellation left a final destination."
    $partials = @(Get-ChildItem -LiteralPath (
        Split-Path $destination) -Filter "*.mfr-partial-*" -ErrorAction SilentlyContinue)
    Assert-Acceptance ($partials.Count -eq 0) `
        "Cancellation left a partial destination."

    return [ordered]@{
        transfer = $transfer
        paused = Get-Content -LiteralPath $paused -Raw | ConvertFrom-Json
        partialCount = $partials.Count
    }
}

Invoke-Scenario "CRASH-01/CRASH-02" {
    $root = Join-Path $runRoot "crash"
    $source = Join-Path $root "source\Crash.mkv"
    $destination = Join-Path $root "destination\Crash.mkv"
    $journal = Join-Path $root "journals"
    $paused = Join-Path $root "paused.json"
    $release = Join-Path $root "release"
    $resultPath = Join-Path $root "host-result.json"
    New-FixtureFile -Path $source -SizeMB $InterruptionFileSizeMB

    $argumentList = @(
        "transfer", "--operation", "Copy",
        "--source", $source,
        "--destination", $destination,
        "--journal", $journal,
        "--pause-at-bytes", "1048576",
        "--paused-marker", $paused,
        "--release-marker", $release,
        "--result-path", $resultPath) -join " "
    $process = Start-Process `
        -FilePath $script:hostPath `
        -ArgumentList $argumentList `
        -WindowStyle Hidden `
        -PassThru
    Wait-ForPath -Path $paused
    $actualProcess = Get-Process -Id $process.Id
    Assert-Acceptance (
        $actualProcess.Path -eq $script:hostPath) `
        "The paused process was not the expected acceptance host."
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()

    $firstInspection = Invoke-AcceptanceHost @(
        "inspect-recovery", "--journal", $journal)
    Assert-Acceptance ($firstInspection.count -eq 1) `
        "The interrupted operation was not detected."
    $operation = $firstInspection.operations[0]
    Assert-Acceptance $operation.canRollback `
        "The interrupted operation was not safely recoverable."

    $recovery = Invoke-AcceptanceHost @(
        "recover", "--journal", $journal,
        "--id", $operation.id,
        "--action", "Rollback")
    Assert-Acceptance $recovery.success "Crash recovery failed."
    Assert-Acceptance (Test-Path -LiteralPath $source) `
        "Crash recovery removed its source."
    Assert-Acceptance (-not (Test-Path -LiteralPath $destination)) `
        "Crash recovery left a final destination."
    $partials = @(Get-ChildItem -LiteralPath (
        Split-Path $destination) -Filter "*.mfr-partial-*" -ErrorAction SilentlyContinue)
    Assert-Acceptance ($partials.Count -eq 0) `
        "Crash recovery left a partial destination."

    $secondInspection = Invoke-AcceptanceHost @(
        "inspect-recovery", "--journal", $journal)
    Assert-Acceptance ($secondInspection.count -eq 0) `
        "Recovered operation was presented again after relaunch."

    return [ordered]@{
        firstInspection = $firstInspection
        recovery = $recovery
        secondInspection = $secondInspection
        partialCount = $partials.Count
    }
}

$failed = @($results | Where-Object result -eq "Fail")
$record = [ordered]@{
    schemaVersion = 1
    runId = $runId
    nonce = $nonce
    runRoot = $runRoot
    markerPath = $markerPath
    hostPath = $hostPath
    hostSha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).
        Hash.ToLowerInvariant()
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
    knownTimestampUtc = $knownTimestamp.ToString("O")
    interruptionFileSizeMB = $InterruptionFileSizeMB
    result = if ($failed.Count -eq 0) { "Pass" } else { "Fail" }
    scenarios = $results
}
$recordPath = Join-Path $runRoot "file-operation-acceptance.json"
[IO.File]::WriteAllText(
    $recordPath,
    ($record | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))

Write-Output "File-operation acceptance: $($record.result)"
Write-Output "Evidence: $recordPath"
if ($failed.Count -gt 0) {
    foreach ($failure in $failed) {
        Write-Error "$($failure.id): $($failure.error)"
    }
    exit 1
}
