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

function Assert-Acceptance {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Get-SafeBasePath {
    param([Parameter(Mandatory)][string] $Path)
    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ($expanded -notmatch '^[A-Za-z]:[\\/]') {
        throw "Root must be an absolute local path: '$Path'."
    }
    $full = [IO.Path]::GetFullPath($expanded)
    $pathRoot = [IO.Path]::GetPathRoot($full)
    if ($full.TrimEnd('\', '/') -eq $pathRoot.TrimEnd('\', '/')) {
        throw "A drive root is too broad for UNC acceptance: '$full'."
    }
    return $full
}

function New-Signal {
    param([Parameter(Mandatory)][string] $Path)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    $stream.Dispose()
}

function Wait-SignalOrError {
    param(
        [Parameter(Mandatory)][string] $Signal,
        [Parameter(Mandatory)][string] $ErrorSignal,
        [int] $Seconds = 90
    )
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $ErrorSignal) {
            $message = Get-Content -LiteralPath $ErrorSignal -Raw
            throw "Disposable SMB controller failed: $message"
        }
        if (Test-Path -LiteralPath $Signal) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for '$Signal'."
}

function New-FixtureFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [int] $SizeMB = 0,
        [string] $Content = "Media File Renamer UNC acceptance fixture"
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
    $timestamp = [datetime]"2020-02-03T04:05:06Z"
    [IO.File]::SetCreationTimeUtc($Path, $timestamp)
    [IO.File]::SetLastWriteTimeUtc($Path, $timestamp)
}

function Get-FileEvidence {
    param([Parameter(Mandatory)][string] $Path)
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $Path
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
    foreach ($property in @(
        "length", "creationTimeUtc", "lastWriteTimeUtc",
        "attributes", "sha256")) {
        Assert-Acceptance (
            $Expected.$property -eq $Actual.$property) `
            "$Context $property changed."
    }
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

$base = Get-SafeBasePath $Root
$script:hostPath = [IO.Path]::GetFullPath($AcceptanceHostPath)
Assert-Acceptance (
    (Test-Path -LiteralPath $script:hostPath -PathType Leaf)) `
    "Build the acceptance host first: '$script:hostPath'."

$nonce = [Guid]::NewGuid().ToString("N")
$runId = "MFR-UncAcceptance-{0}-{1}" -f `
    (Get-Date -Format "yyyyMMdd-HHmmss"), `
    $nonce.Substring(0, 10)
$runRoot = Join-Path $base $runId
$sharePath = Join-Path $runRoot "share"
$signalRoot = Join-Path $runRoot "signals"
[IO.Directory]::CreateDirectory($sharePath) | Out-Null
[IO.Directory]::CreateDirectory($signalRoot) | Out-Null
$markerPath = Join-Path $runRoot ".mfr-acceptance-$nonce"
[IO.File]::WriteAllText(
    $markerPath,
    $nonce,
    [Text.UTF8Encoding]::new($false))

$shareName = "MFRACC-$($nonce.Substring(0, 10))"
$signals = [ordered]@{}
foreach ($name in @(
    "ready", "interruptRequest", "removed", "restoreRequest",
    "restored", "finishRequest", "finished", "error")) {
    $signals[$name] = Join-Path $signalRoot $name
}

$controllerManifest = [ordered]@{
    schemaVersion = 1
    runRoot = $runRoot
    sharePath = $sharePath
    shareName = $shareName
    currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    nonce = $nonce
    markerPath = $markerPath
    signals = $signals
}
$manifestPath = Join-Path $runRoot "smb-controller.json"
[IO.File]::WriteAllText(
    $manifestPath,
    ($controllerManifest | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

$controllerScript = (
    Resolve-Path .\scripts\acceptance\Invoke-DisposableSmbController.ps1).Path
$powerShell = Join-Path $PSHOME "powershell.exe"
$controllerArguments = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$controllerScript`"",
    "-ManifestPath", "`"$manifestPath`"") -join " "

$controller = $null
$script:results = [Collections.Generic.List[object]]::new()
$controllerReady = $false
try {
    $controller = Start-Process `
        -FilePath $powerShell `
        -ArgumentList $controllerArguments `
        -Verb RunAs `
        -WindowStyle Hidden `
        -PassThru
    Wait-SignalOrError $signals.ready $signals.error
    $controllerReady = $true
    $uncRoot = "\\$env:COMPUTERNAME\$shareName"
    Assert-Acceptance (Test-Path -LiteralPath $uncRoot) `
        "The disposable UNC share is not reachable."

    Invoke-Scenario "UNC-01" {
        $source = Join-Path $runRoot "copy-source\Unc.Copy.mkv"
        $destination = Join-Path $uncRoot "copy\Unc Copy.mkv"
        $journal = Join-Path $runRoot "copy-journals"
        New-FixtureFile $source -Content "UNC copy $nonce"
        $before = Get-FileEvidence $source
        $transfer = Invoke-AcceptanceHost @(
            "transfer", "--operation", "Copy",
            "--source", $source,
            "--destination", $destination,
            "--journal", $journal)
        Assert-Acceptance $transfer.success "UNC copy failed."
        Assert-MetadataEqual $before (Get-FileEvidence $destination) `
            "UNC copy"
        $undo = Invoke-AcceptanceHost @(
            "undo", "--journal", $journal)
        Assert-Acceptance $undo.success "UNC copy Undo failed."
        Assert-Acceptance (-not (Test-Path -LiteralPath $destination)) `
            "UNC copy Undo left its destination."
        return [ordered]@{ transfer = $transfer; undo = $undo }
    }

    Invoke-Scenario "UNC-02" {
        $source = Join-Path $runRoot "move-source\Unc.Move.mkv"
        $destination = Join-Path $uncRoot "move\Unc Move.mkv"
        $journal = Join-Path $runRoot "move-journals"
        New-FixtureFile $source -Content "UNC move $nonce"
        $before = Get-FileEvidence $source
        $transfer = Invoke-AcceptanceHost @(
            "transfer", "--operation", "Move",
            "--source", $source,
            "--destination", $destination,
            "--journal", $journal)
        Assert-Acceptance $transfer.success "UNC move failed."
        Assert-Acceptance (-not (Test-Path -LiteralPath $source)) `
            "UNC move retained its source."
        Assert-MetadataEqual $before (Get-FileEvidence $destination) `
            "UNC move"
        $undo = Invoke-AcceptanceHost @(
            "undo", "--journal", $journal)
        Assert-Acceptance $undo.success "UNC move Undo failed."
        Assert-MetadataEqual $before (Get-FileEvidence $source) `
            "UNC move restore"
        return [ordered]@{ transfer = $transfer; undo = $undo }
    }

    Invoke-Scenario "NET-01/NET-02" {
        $source = Join-Path $runRoot "network-source\Network.mkv"
        $destination = Join-Path $uncRoot "network\Network.mkv"
        $journal = Join-Path $runRoot "network-journals"
        $paused = Join-Path $runRoot "network-paused.json"
        $release = Join-Path $runRoot "network-release"
        $resultPath = Join-Path $runRoot "network-host-result.json"
        New-FixtureFile $source -SizeMB $InterruptionFileSizeMB

        $argumentList = @(
            "transfer", "--operation", "Copy",
            "--source", $source,
            "--destination", $destination,
            "--journal", $journal,
            "--pause-at-bytes", "1048576",
            "--paused-marker", $paused,
            "--release-marker", $release,
            "--result-path", $resultPath) -join " "
        $transferProcess = Start-Process `
            -FilePath $script:hostPath `
            -ArgumentList $argumentList `
            -WindowStyle Hidden `
            -PassThru
        Wait-SignalOrError $paused $signals.error 45
        $actualHost = Get-Process -Id $transferProcess.Id
        Assert-Acceptance ($actualHost.Path -eq $script:hostPath) `
            "The paused process was not the expected acceptance host."

        New-Signal $signals.interruptRequest
        Wait-SignalOrError $signals.removed $signals.error
        Assert-Acceptance (-not (Test-Path -LiteralPath $uncRoot)) `
            "The disposable share remained reachable during interruption."
        New-Signal $release
        if (-not $transferProcess.WaitForExit(60000)) {
            Stop-Process -Id $transferProcess.Id -Force
            throw "The interrupted transfer did not stop within 60 seconds."
        }
        Assert-Acceptance ($transferProcess.ExitCode -ne 0) `
            "The transfer unexpectedly succeeded while its share was removed."

        New-Signal $signals.restoreRequest
        Wait-SignalOrError $signals.restored $signals.error
        Assert-Acceptance (Test-Path -LiteralPath $uncRoot) `
            "The disposable share did not return."

        $hostResult = Get-Content -LiteralPath $resultPath -Raw |
            ConvertFrom-Json
        Assert-Acceptance (-not $hostResult.success) `
            "Network interruption was not reported as a failure."
        $inspection = Invoke-AcceptanceHost @(
            "inspect-recovery", "--journal", $journal)
        Assert-Acceptance ($inspection.count -eq 1) `
            "Network interruption did not retain recoverable journal state."
        $operation = $inspection.operations[0]
        Assert-Acceptance $operation.canRollback `
            "Network recovery was not safe after the share returned."
        $recovery = Invoke-AcceptanceHost @(
            "recover", "--journal", $journal,
            "--id", $operation.id,
            "--action", "Rollback")
        Assert-Acceptance $recovery.success `
            "Network interruption recovery failed."
        Assert-Acceptance (Test-Path -LiteralPath $source) `
            "Network recovery removed its source."
        Assert-Acceptance (-not (Test-Path -LiteralPath $destination)) `
            "Network recovery left a final destination."
        $partials = @(Get-ChildItem `
            -LiteralPath (Split-Path $destination) `
            -Filter "*.mfr-partial-*" `
            -ErrorAction SilentlyContinue)
        Assert-Acceptance ($partials.Count -eq 0) `
            "Network recovery left a partial destination."
        $secondInspection = Invoke-AcceptanceHost @(
            "inspect-recovery", "--journal", $journal)
        Assert-Acceptance ($secondInspection.count -eq 0) `
            "Recovered network operation was offered again."

        return [ordered]@{
            hostResult = $hostResult
            inspection = $inspection
            recovery = $recovery
            secondInspection = $secondInspection
            partialCount = $partials.Count
        }
    }
} finally {
    if ($controllerReady -and
        -not (Test-Path -LiteralPath $signals.finished)) {
        if ((Test-Path -LiteralPath $signals.removed) -and
            -not (Test-Path -LiteralPath $signals.restoreRequest)) {
            New-Signal $signals.restoreRequest
            try {
                Wait-SignalOrError $signals.restored $signals.error 45
            } catch {
                # The controller error is recorded below.
            }
        }
        if (-not (Test-Path -LiteralPath $signals.finishRequest)) {
            New-Signal $signals.finishRequest
        }
        try {
            Wait-SignalOrError $signals.finished $signals.error 45
        } catch {
            $script:results.Add([ordered]@{
                id = "SMB-CLEANUP"
                result = "Fail"
                error = $_.Exception.Message
            })
        }
    }
    if ($null -ne $controller -and -not $controller.HasExited) {
        $controller.WaitForExit(10000) | Out-Null
    }
}

$failed = @($results | Where-Object result -eq "Fail")
$record = [ordered]@{
    schemaVersion = 1
    runId = $runId
    nonce = $nonce
    runRoot = $runRoot
    shareName = $shareName
    sharePath = $sharePath
    hostPath = $hostPath
    hostSha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).
        Hash.ToLowerInvariant()
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
    result = if ($failed.Count -eq 0) { "Pass" } else { "Fail" }
    scenarios = $results
}
$recordPath = Join-Path $runRoot "unc-acceptance.json"
[IO.File]::WriteAllText(
    $recordPath,
    ($record | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))

Write-Output "UNC acceptance: $($record.result)"
Write-Output "Evidence: $recordPath"
if ($failed.Count -gt 0) {
    foreach ($failure in $failed) {
        Write-Error "$($failure.id): $($failure.error)"
    }
    exit 1
}
