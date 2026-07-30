[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ExecutablePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedSha256,

    [Parameter(Mandatory)]
    [string] $ExpectedProductVersion,

    [Parameter(Mandatory)]
    [string] $ArtifactPath,

    [ValidateRange(10, 90)]
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if ($null -eq ("MediaFileRenamerProviderAcceptanceNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MediaFileRenamerProviderAcceptanceNative
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);
}
'@
}

function Get-TopLevelWindows {
    param([int] $ProcessId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    $collection =
        [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
    $windows = @()
    for ($index = 0; $index -lt $collection.Count; $index++) {
        if ($collection[$index].Current.ControlType -eq
            [System.Windows.Automation.ControlType]::Window) {
            $windows += $collection[$index]
        }
    }
    return $windows
}

function Wait-UiaWindow {
    param(
        [Parameter(Mandatory)][int] $ProcessId,
        [Parameter(Mandatory)][string] $Name,
        [switch] $Absent
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $match = Get-TopLevelWindows -ProcessId $ProcessId |
            Where-Object { $_.Current.Name -eq $Name } |
            Select-Object -First 1
        if ($Absent -and $null -eq $match) {
            return $null
        }
        if (-not $Absent -and $null -ne $match) {
            return $match
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($Absent) {
        throw "Window '$Name' did not close."
    }
    throw "Window '$Name' did not appear."
}

function Find-UiaElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Root,
        [Parameter(Mandatory)]
        [string] $AutomationId
    )

    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId))
}

function Find-ProcessElementByName {
    param(
        [Parameter(Mandatory)][int] $ProcessId,
        [Parameter(Mandatory)][string] $Name
    )

    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem))
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $match = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $match) {
            return $match
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Element '$Name' did not appear."
}

function Invoke-UiaElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element
    )

    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-UiaName {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element,
        [Parameter(Mandatory)]
        [string] $ExpectedName
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Element.Current.Name -eq $ExpectedName) {
            return $ExpectedName
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Provider test did not report the expected success message."
}

function Wait-UiaElementEnabled {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Element.Current.IsEnabled) {
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Element '$($Element.Current.AutomationId)' did not become enabled."
}

function Open-SyntheticMediaFile {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $MainWindow,
        [Parameter(Mandatory)]
        [string] $Path
    )

    Invoke-UiaElement (
        Find-UiaElement -Root $MainWindow -AutomationId "AddFilesButton")
    $dialog = Wait-UiaWindow -ProcessId $process.Id -Name "Open"
    $fileNameCombo = Find-UiaElement -Root $dialog -AutomationId "1148"
    $fileNameEdit = $fileNameCombo.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit))
    if ($null -eq $fileNameEdit) {
        throw "The native Open dialog did not expose its filename edit."
    }

    [void][MediaFileRenamerProviderAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$dialog.Current.NativeWindowHandle)
    $fileNameEdit.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    [System.Windows.Forms.SendKeys]::SendWait($Path)
    Start-Sleep -Milliseconds 150

    $open = $dialog.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                "1"),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))
    Invoke-UiaElement $open
    Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Open" `
        -Absent | Out-Null
}

function Get-UiaRows {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Grid
    )

    return $Grid.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem))
}

function Open-MatchPickerForFirstRow {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $MainWindow
    )

    $grid = Find-UiaElement -Root $MainWindow -AutomationId "OriginalGrid"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $rows = @(Get-UiaRows -Grid $grid)
        if ($rows.Count -gt 0) {
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($rows.Count -eq 0) {
        throw "No synthetic media row appeared."
    }

    $selection = $rows[0].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    $choose = Find-UiaElement `
        -Root $MainWindow `
        -AutomationId "ChooseSelectedButton"
    Wait-UiaElementEnabled -Element $choose
    Invoke-UiaElement $choose
    return Wait-UiaWindow -ProcessId $process.Id -Name "Choose Match"
}

function Wait-MatchCandidate {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Picker,
        [Parameter(Mandatory)]
        [scriptblock] $Predicate
    )

    $list = Find-UiaElement -Root $Picker -AutomationId "CandidateList"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $items = $list.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem))
        for ($index = 0; $index -lt $items.Count; $index++) {
            if (& $Predicate $items[$index].Current.Name) {
                return $items[$index]
            }
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The expected metadata candidate did not appear."
}

if ([Threading.Thread]::CurrentThread.ApartmentState -ne
    [Threading.ApartmentState]::STA) {
    throw "Run this acceptance check with Windows PowerShell 5.1 and -STA."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedArtifact = [IO.Path]::GetFullPath($ArtifactPath)
if ([IO.File]::Exists($resolvedArtifact)) {
    throw "ArtifactPath already exists: '$resolvedArtifact'."
}
$artifactDirectory = [IO.Path]::GetDirectoryName($resolvedArtifact)
if ([string]::IsNullOrWhiteSpace($artifactDirectory)) {
    throw "ArtifactPath must include a parent directory."
}
[IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null

$hash = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash
if ($hash -ne $ExpectedSha256.ToUpperInvariant()) {
    throw "Executable SHA-256 mismatch."
}
$version = (Get-Item -LiteralPath $resolvedExecutable).VersionInfo.ProductVersion
if ($version -ne $ExpectedProductVersion) {
    throw "Executable product version mismatch."
}

$settingsPath = Join-Path `
    ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)) `
    "MediaFileRenamer\settings.json"
if (-not [IO.File]::Exists($settingsPath)) {
    throw "Personal Media File Renamer settings do not exist. No provider test was run."
}

if (@(Get-Process -Name "MediaFileRenamer" -ErrorAction SilentlyContinue).Count -gt 0) {
    throw "Close Media File Renamer before running provider acceptance."
}

$process = $null
$checks = New-Object System.Collections.ArrayList
try {
    $process = Start-Process -FilePath $resolvedExecutable -PassThru
    $main = Wait-UiaWindow -ProcessId $process.Id -Name "Media File Renamer"
    Start-Sleep -Milliseconds 500
    $recovery = Get-TopLevelWindows -ProcessId $process.Id |
        Where-Object {
            $_.Current.Name -eq "Interrupted operation recovery center"
        } |
        Select-Object -First 1
    if ($null -ne $recovery) {
        $closeRecovery = Find-UiaElement `
            -Root $recovery `
            -AutomationId "CloseButton"
        Invoke-UiaElement $closeRecovery
        Wait-UiaWindow `
            -ProcessId $process.Id `
            -Name "Interrupted operation recovery center" `
            -Absent | Out-Null
    }

    $mainHandle = [IntPtr]$main.Current.NativeWindowHandle
    [void][MediaFileRenamerProviderAcceptanceNative]::ShowWindow($mainHandle, 9)
    [void][MediaFileRenamerProviderAcceptanceNative]::SetForegroundWindow(
        $mainHandle)
    $main.SetFocus()
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait("%f")
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait("s")

    $settings = Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Media File Renamer settings"
    $tmdbStatus = Find-UiaElement `
        -Root $settings `
        -AutomationId "TmdbTestStatusTextBlock"
    $tvdbStatus = Find-UiaElement `
        -Root $settings `
        -AutomationId "TvdbTestStatusTextBlock"

    Invoke-UiaElement (
        Find-UiaElement -Root $settings -AutomationId "TestTmdbButton")
    $tmdbMessage = Wait-UiaName `
        -Element $tmdbStatus `
        -ExpectedName "Connected to TMDB successfully."
    [void]$checks.Add([ordered]@{
        id = "personal-provider.tmdb"
        passed = $true
        message = $tmdbMessage
    })

    Invoke-UiaElement (
        Find-UiaElement -Root $settings -AutomationId "TestTvdbButton")
    $tvdbMessage = Wait-UiaName `
        -Element $tvdbStatus `
        -ExpectedName "Connected to TVDB successfully."
    [void]$checks.Add([ordered]@{
        id = "personal-provider.tvdb"
        passed = $true
        message = $tvdbMessage
    })

    $settings.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Media File Renamer settings" `
        -Absent | Out-Null

    $fixtureRoot = Join-Path $artifactDirectory (
        "personal-provider-fixtures-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null

    $moviePath = Join-Path `
        $fixtureRoot `
        "Blade.Runner.1982.Directors.Cut.1080p.mkv"
    [IO.File]::WriteAllBytes($moviePath, [byte[]](0..63))
    Open-SyntheticMediaFile -MainWindow $main -Path $moviePath
    $picker = Open-MatchPickerForFirstRow -MainWindow $main
    $movieCandidate = Wait-MatchCandidate `
        -Picker $picker `
        -Predicate {
            param($name)
            $name.Contains("Blade Runner") `
                -and $name.Contains("1982") `
                -and $name.Contains("Movie")
        }
    [void]$checks.Add([ordered]@{
        id = "personal-provider.tmdb-search"
        passed = $true
        message = "Packaged search returned the 1982 Blade Runner movie candidate."
    })
    Invoke-UiaElement (
        Find-UiaElement -Root $picker -AutomationId "CancelButton")
    Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Choose Match" `
        -Absent | Out-Null
    Invoke-UiaElement (
        Find-UiaElement -Root $main -AutomationId "ClearButton")

    $specialsRoot = Join-Path `
        $fixtureRoot `
        "Curious George TV Series 2006\Specials"
    [IO.Directory]::CreateDirectory($specialsRoot) | Out-Null
    $episodeSeven = Join-Path $specialsRoot "Curious.George.S00E07.mkv"
    $episodeEight = Join-Path $specialsRoot "Curious.George.S00E08.mkv"
    [IO.File]::WriteAllBytes($episodeSeven, [byte[]](0..63))
    [IO.File]::WriteAllBytes($episodeEight, [byte[]](0..63))
    Open-SyntheticMediaFile -MainWindow $main -Path $episodeSeven
    Open-SyntheticMediaFile -MainWindow $main -Path $episodeEight

    $picker = Open-MatchPickerForFirstRow -MainWindow $main
    $showCandidate = Wait-MatchCandidate `
        -Picker $picker `
        -Predicate {
            param($name)
            $name.Contains("Curious George") `
                -and $name.Contains("2006") `
                -and $name.Contains("TV")
        }
    $showSelection = $showCandidate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $showSelection.Select()
    $useSelected = Find-UiaElement `
        -Root $picker `
        -AutomationId "UseSelectedButton"
    Wait-UiaElementEnabled -Element $useSelected
    Invoke-UiaElement $useSelected
    Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Choose Match" `
        -Absent | Out-Null

    $choose = Find-UiaElement `
        -Root $main `
        -AutomationId "ChooseSelectedButton"
    Wait-UiaElementEnabled -Element $choose
    $reviewGrid = Find-UiaElement `
        -Root $main `
        -AutomationId "OriginalGrid"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $proposedRows = @(Get-UiaRows -Grid $reviewGrid)
        $rowNames = @(
            for ($index = 0; $index -lt $proposedRows.Count; $index++) {
                $proposedRows[$index].Current.Name
            }
        )
        $episodeSevenMatched = @(
            $rowNames | Where-Object {
                $_.Contains("Curious George Comes to America")
            }
        ).Count -gt 0
        $episodeEightMatched = @(
            $rowNames | Where-Object {
                $_.Contains("Curious George Goes to the Hospital")
            }
        ).Count -gt 0
        if ($episodeSevenMatched -and $episodeEightMatched) {
            break
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not ($episodeSevenMatched -and $episodeEightMatched)) {
        throw "The packaged Curious George specials did not resolve to the expected TVDB episode titles. Actual rows: $($rowNames -join ' | ')"
    }

    [void]$checks.Add([ordered]@{
        id = "personal-provider.special-titles"
        passed = $true
        message = "Default-order S00E07 and S00E08 resolved to the expected Curious George titles."
    })
    Invoke-UiaElement (
        Find-UiaElement -Root $main -AutomationId "ClearButton")

    $officialProbe = Join-Path `
        $fixtureRoot `
        "Curious George TVDB Official Probe\Specials\Curious.George.S00E07.mkv"
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($officialProbe)) | Out-Null
    [IO.File]::WriteAllBytes($officialProbe, [byte[]](0..63))
    Open-SyntheticMediaFile -MainWindow $main -Path $officialProbe

    $originalGrid = Find-UiaElement `
        -Root $main `
        -AutomationId "OriginalGrid"
    $originalRows = @(Get-UiaRows -Grid $originalGrid)
    $rowSelection = $originalRows[0].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $rowSelection.Select()
    $episodeOrder = Find-UiaElement `
        -Root $main `
        -AutomationId "EpisodeOrderComboBox"
    [void][MediaFileRenamerProviderAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$main.Current.NativeWindowHandle)
    $episodeOrder.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{HOME}{DOWN}")

    $picker = Open-MatchPickerForFirstRow -MainWindow $main
    $showCandidate = Wait-MatchCandidate `
        -Picker $picker `
        -Predicate {
            param($name)
            $name.Contains("Curious George") `
                -and $name.Contains("2006") `
                -and $name.Contains("TV")
        }
    $showSelection = $showCandidate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $showSelection.Select()
    $useSelected = Find-UiaElement `
        -Root $picker `
        -AutomationId "UseSelectedButton"
    Wait-UiaElementEnabled -Element $useSelected
    Invoke-UiaElement $useSelected
    Wait-UiaWindow `
        -ProcessId $process.Id `
        -Name "Choose Match" `
        -Absent | Out-Null
    Wait-UiaElementEnabled -Element (
        Find-UiaElement -Root $main -AutomationId "ChooseSelectedButton")

    $reviewGrid = Find-UiaElement `
        -Root $main `
        -AutomationId "OriginalGrid"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $fallbackRows = @(Get-UiaRows -Grid $reviewGrid)
        $fallbackMatched = @(
            $fallbackRows | Where-Object { $_.Current.Name.Contains("TVDB") }
        ).Count -gt 0
        if ($fallbackMatched) {
            break
        }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $fallbackMatched) {
        throw "The Official-order probe did not report TVDB fallback."
    }
    [void]$checks.Add([ordered]@{
        id = "personal-provider.tvdb-fallback"
        passed = $true
        message = "Official episode order explicitly resolved through TVDB fallback."
    })
    Invoke-UiaElement (
        Find-UiaElement -Root $main -AutomationId "ClearButton")
} catch {
    [void]$checks.Add([ordered]@{
        id = "personal-provider.unhandled"
        passed = $false
        message = $_.Exception.Message
    })
} finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                [void]$process.CloseMainWindow()
                if (-not $process.WaitForExit(3000)) {
                    Stop-Process -Id $process.Id -Force
                    $process.WaitForExit()
                }
            }
        } catch {
            [void]$checks.Add([ordered]@{
                id = "personal-provider.process-cleanup"
                passed = $false
                message = "The exact acceptance process did not close cleanly."
            })
        }
    }
}

$logPath = Join-Path `
    ([IO.Path]::GetDirectoryName($settingsPath)) `
    "Logs\MediaFileRenamer.log"
$logPassed = [IO.File]::Exists($logPath)
if ($logPassed) {
    $logText = [IO.File]::ReadAllText($logPath)
    $profilePath = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::UserProfile)
    $logPassed =
        -not $logText.Contains($profilePath) `
        -and $logText -notmatch '(?i)(api[_-]?key=|authorization:|bearer\s+|subscriber\s+pin)'
}
[void]$checks.Add([ordered]@{
    id = "personal-provider.redacted-log"
    passed = $logPassed
    message = if ($logPassed) {
        "The diagnostic log exists and contains no credential/header or unredacted profile-path patterns."
    } else {
        "The diagnostic log is missing or contains a sensitive pattern."
    }
})

$failed = @($checks | Where-Object { -not $_.passed })
$result = [ordered]@{
    schemaVersion = 1
    testedAtUtc = [DateTime]::UtcNow.ToString("O")
    executable = [ordered]@{
        sha256 = $hash
        productVersion = $version
    }
    passed = $failed.Count -eq 0
    checks = @($checks)
}
[IO.File]::WriteAllText(
    $resolvedArtifact,
    ($result | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

Write-Output "Personal provider acceptance: $resolvedArtifact"
if ($failed.Count -gt 0) {
    $failed | ForEach-Object {
        Write-Error "$($_.id): $($_.message)"
    }
    exit 1
}
