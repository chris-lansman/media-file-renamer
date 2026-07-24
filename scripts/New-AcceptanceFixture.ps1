[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Root,

    [string] $UncRoot,

    [ValidateRange(0, 4096)]
    [int] $StressFileSizeMB = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fixtureTimestamp = [DateTime]::SpecifyKind(
    [DateTime]::ParseExact(
        "2020-02-03T04:05:06",
        "yyyy-MM-ddTHH:mm:ss",
        [Globalization.CultureInfo]::InvariantCulture),
    [DateTimeKind]::Utc)

function Get-SafeAbsolutePath {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [switch] $RequireUnc
    )

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    $isDriveAbsolute = $expandedPath -match '^[A-Za-z]:[\\/]'
    $isUnc = $expandedPath -match '^\\\\[^\\]+\\[^\\]+(?:\\|$)'
    if (-not ($isDriveAbsolute -or $isUnc)) {
        throw "The fixture root must be an absolute path: '$Path'."
    }

    $fullPath = [IO.Path]::GetFullPath($expandedPath)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.TrimEnd('\', '/') -eq $pathRoot.TrimEnd('\', '/')) {
        throw "A drive or share root is too broad for an acceptance fixture: '$fullPath'."
    }

    if ($RequireUnc -and -not $isUnc) {
        throw "UncRoot must be a UNC path such as \\server\disposable-share\acceptance."
    }

    return $fullPath
}

function New-FixtureFile {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Content,

        [int] $SizeMB = 0
    )

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
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

    [IO.File]::SetCreationTimeUtc($Path, $fixtureTimestamp)
    [IO.File]::SetLastWriteTimeUtc($Path, $fixtureTimestamp)
}

function New-RepresentativeSet {
    param(
        [Parameter(Mandatory)]
        [string] $InputRoot,

        [Parameter(Mandatory)]
        [string] $Scenario
    )

    $files = @(
        "Movies\Blade Runner 1982\Blade.Runner.1982.Directors.Cut.1080p.mkv",
        "Movies\Blade Runner 1982\Blade.Runner.1982.Directors.Cut.1080p.en.srt",
        "Movies\Blade Runner 1982\Blade.Runner.1982.Directors.Cut.1080p.nfo",
        "Movies\Blade Runner 1982\Blade.Runner.1982.Directors.Cut.1080p.jpg",
        "TV Shows\Curious George\Specials\Curious.George.S00E07.mkv",
        "TV Shows\Curious George\Specials\Curious.George.S00E07.en.forced.srt",
        "TV Shows\Curious George\Specials\Curious.George.S00E07.nfo",
        "TV Shows\Curious George\Specials\Curious.George.S00E08.mkv",
        "TV Shows\Curious George\Specials\Curious.George.S00E08.en.srt",
        "TV Shows\Example Show\Season 01\Example.Show.S01E01-E02.Opening.Night.mkv",
        "TV Shows\Example Show\Season 01\Example.Show.S01E01-E02.Opening.Night.en.srt",
        "TV Shows\Example Show\Season 01\Example.Show.S01E03.pt2.mkv",
        "TV Shows\Daily Show\Daily.Show.2026-07-23.mkv",
        "TV Shows\Anime Show (TV Series 2024)\Season 01\012 - The Promise.mkv"
    )

    foreach ($relativePath in $files) {
        $path = Join-Path $InputRoot $relativePath
        New-FixtureFile -Path $path -Content "Media File Renamer acceptance fixture: $Scenario/$relativePath"
    }
}

$localBase = Get-SafeAbsolutePath -Path $Root
$uncBase = $null
if (-not [string]::IsNullOrWhiteSpace($UncRoot)) {
    $uncBase = Get-SafeAbsolutePath -Path $UncRoot -RequireUnc
}

$runId = "MediaFileRenamer-Acceptance-{0}-{1}" -f `
    (Get-Date -Format "yyyyMMdd-HHmmss"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$runRoot = Join-Path $localBase $runId
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

$scenarioNames = @("local-copy", "local-move", "collision")
$scenarios = [ordered]@{}
foreach ($scenarioName in $scenarioNames) {
    $inputRoot = Join-Path $runRoot "inputs\$scenarioName"
    $destinationRoot = Join-Path $runRoot "destinations\$scenarioName"
    [IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
    New-RepresentativeSet -InputRoot $inputRoot -Scenario $scenarioName
    $scenarios[$scenarioName] = [ordered]@{
        input = $inputRoot
        destination = $destinationRoot
    }
}

foreach ($scenarioName in @("cancellation", "network")) {
    $inputRoot = Join-Path $runRoot "inputs\$scenarioName"
    $destinationRoot = Join-Path $runRoot "destinations\$scenarioName"
    [IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
    $videoPath = Join-Path $inputRoot "Movies\Interruption Sample (2026)\Interruption.Sample.2026.mkv"
    New-FixtureFile `
        -Path $videoPath `
        -Content "Media File Renamer acceptance fixture: $scenarioName" `
        -SizeMB $StressFileSizeMB
    New-FixtureFile `
        -Path ([IO.Path]::ChangeExtension($videoPath, ".en.srt")) `
        -Content "Media File Renamer acceptance subtitle fixture: $scenarioName"
    $scenarios[$scenarioName] = [ordered]@{
        input = $inputRoot
        destination = $destinationRoot
    }
}

$uncDestination = $null
if ($null -ne $uncBase) {
    $uncDestination = Join-Path $uncBase $runId
    [IO.Directory]::CreateDirectory($uncDestination) | Out-Null
}

$manifest = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
    fixtureTimestampUtc = $fixtureTimestamp.ToString("O")
    runId = $runId
    runRoot = $runRoot
    stressFileSizeMB = $StressFileSizeMB
    uncDestination = $uncDestination
    scenarios = $scenarios
    notes = @(
        "All files are synthetic disposable fixtures.",
        "The script never stores provider credentials.",
        "The script never deletes or overwrites an existing file.",
        "Remove the uniquely named run folders manually after acceptance is complete."
    )
}

$manifestPath = Join-Path $runRoot "acceptance-manifest.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $manifestPath,
    $manifestJson,
    [Text.UTF8Encoding]::new($false))

Write-Output "Created disposable acceptance fixture:"
Write-Output $runRoot
Write-Output ""
Write-Output "Manifest:"
Write-Output $manifestPath
if ($StressFileSizeMB -eq 0) {
    Write-Warning "Cancellation and interruption files are small. Re-run with -StressFileSizeMB 512 or larger when a longer transfer window is needed."
}
if ($null -eq $uncDestination) {
    Write-Warning "No UNC destination was created. Supply -UncRoot only for a disposable test share."
}
