[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$runRoot = Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

function Find-Control($Root, [string]$Id) {
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}

function Invoke-Control($Control) {
    ([System.Windows.Automation.InvokePattern]$Control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Wait-For([scriptblock]$Condition, [string]$Failure) {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    throw $Failure
}

$results = @()
foreach ($scenario in @('Move', 'Copy', 'Collision')) {
    $dataRoot = Join-Path $runRoot $scenario
    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $source = Join-Path $dataRoot 'Example.Movie.2024.mkv'
    $subtitle = Join-Path $dataRoot 'Example.Movie.2024.en.srt'
    $output = Join-Path $dataRoot 'output'
    [IO.File]::WriteAllText($source, 'Disposable direct-transfer media')
    [IO.File]::WriteAllText($subtitle, 'Disposable companion subtitle')
    $operationIndex = if ($scenario -eq 'Copy') { 1 } else { 0 }
    @{ SchemaVersion = 2; UseTmdbLookup = $false; DefaultOutputFolder = $output } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json')
    @{
        Version = 1; Width = 1360; Height = 860; Preset = 1; Operation = $operationIndex
        OutputFolder = $output; ColumnWidths = @(); SelectedSource = $source
        Items = @(@{ SourcePath = $source; MatchedTitle = 'Example Movie'; MediaType = 'Movie'; Year = 2024; Extension = '.mkv' })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dataRoot 'workspace.json')
    $process = $null
    try {
        $process = Start-Process -FilePath $executable -ArgumentList @('--data-root', ('"' + $dataRoot + '"')) -PassThru -WindowStyle Hidden
        Wait-For {
            $process.Refresh()
            if ($process.HasExited) { throw "App exited during $scenario." }
            $process.MainWindowHandle -ne 0
        } 'The app did not open.'
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        Wait-For {
            $grid = Find-Control $window 'OriginalGrid'
            $null -ne $grid -and $grid.Current.IsEnabled -and $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::DataItem)).Count -eq 1
        } 'The disposable saved item did not restore.'
        $apply = Find-Control $window 'RenameButton'
        if ($apply.Current.IsEnabled) { throw 'Restored media bypassed the fresh-review gate.' }
        $editor = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Edit media details'))
        ([System.Windows.Automation.ExpandCollapsePattern]$editor.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Invoke-Control (Find-Control $window 'ConfirmManualDetailsButton')
        Wait-For { $apply.Current.IsEnabled } 'Confirming the fixture did not make the batch ready.'
        Wait-For {
            $saved = Get-Content -Raw -LiteralPath (Join-Path $dataRoot 'workspace.json') | ConvertFrom-Json
            $saved.Items.Count -eq 1 -and $saved.Items[0].Status -match '^Manual' -and $saved.Items[0].DestinationPath
        } 'The reviewed destination was not saved.'
        $saved = Get-Content -Raw -LiteralPath (Join-Path $dataRoot 'workspace.json') | ConvertFrom-Json
        $destination = $saved.Items[0].DestinationPath
        if ($scenario -eq 'Collision') {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.File]::WriteAllText($destination, 'Existing destination sentinel')
        }

        # This is the only transfer-action click. Any confirmation dialog will
        # leave the fixture untransferred and fail the bounded wait below.
        Invoke-Control $apply
        if ($scenario -eq 'Collision') {
            Wait-For {
                $saved = Get-Content -Raw -LiteralPath (Join-Path $dataRoot 'workspace.json') | ConvertFrom-Json
                $saved.Items.Count -eq 1 -and $saved.Items[0].Status -match '^Failed'
            } 'The collision was not reported automatically.'
            if ([IO.File]::ReadAllText($destination) -ne 'Existing destination sentinel') { throw 'Collision overwrote the destination.' }
            if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $subtitle)) { throw 'Collision changed source files.' }
        } else {
            Wait-For {
                $files = @(Get-ChildItem -LiteralPath $output -File -Recurse -ErrorAction SilentlyContinue)
                (Test-Path -LiteralPath $destination) -and $files.Count -eq 2 -and -not $apply.Current.IsEnabled -and
                    (Find-Control $window 'OriginalGrid').Current.IsEnabled
            } "$scenario required another click or did not transfer both fixtures."
            if ([IO.File]::ReadAllText($destination) -ne 'Disposable direct-transfer media') { throw 'Media contents changed.' }
            $copiedSubtitle = @(Get-ChildItem -LiteralPath $output -Filter '*.srt' -File -Recurse)
            if ($copiedSubtitle.Count -ne 1 -or [IO.File]::ReadAllText($copiedSubtitle[0].FullName) -ne 'Disposable companion subtitle') { throw 'Companion contents changed.' }
            $sourcesExpected = $scenario -eq 'Copy'
            if ((Test-Path -LiteralPath $source) -ne $sourcesExpected -or (Test-Path -LiteralPath $subtitle) -ne $sourcesExpected) { throw "$scenario left unexpected source files." }
        }
        $results += @{ Scenario = $scenario; Passed = $true; TransferClicks = 1 }
        Write-Host "$scenario passed with one transfer click and no confirmation."
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) { throw 'The app did not close normally.' }
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'results.json')
Write-Host "Direct-transfer evidence: $runRoot"
