[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$artifactDirectory = [IO.Path]::GetFullPath($ArtifactRoot)
$runDirectory = Join-Path $artifactDirectory ([Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$source = Join-Path $runDirectory 'Example.Movie.2024.mkv'
[IO.File]::WriteAllText($source, 'Disposable workspace restoration fixture')
$output = Join-Path $runDirectory 'output'
@{ SchemaVersion = 2; UseTmdbLookup = $false; DefaultOutputFolder = $output } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'settings.json')
@{
    Version = 1; Width = 1024; Height = 768; Preset = 0; Operation = 1
    OutputFolder = $output; ColumnWidths = @(); SelectedSource = $source
    Items = @(@{
        SourcePath = $source; MatchedTitle = 'Edited Example'; MediaType = 'Movie'
        Year = 2024; Extension = '.mkv'; Status = 'Manual choice'
        DestinationPath = (Join-Path $runDirectory 'obsolete.mkv')
    }, @{
        SourcePath = (Join-Path $runDirectory 'Missing.mkv'); MatchedTitle = 'Missing'
        MediaType = 'Movie'; Extension = '.mkv'; Status = 'Manual choice'
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'workspace.json')

for ($attempt = 1; $attempt -le 2; $attempt++) {
    $process = $null
    $rows = $null
    try {
        $process = Start-Process -FilePath $executable -ArgumentList @('--data-root', ('"' + $runDirectory + '"')) -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(20)
        $status = ''
        while ((Get-Date) -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw "Packaged app exited with code $($process.ExitCode)." }
            if ($process.MainWindowHandle -ne 0) {
                $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'StatusTextBlock')
                $statusElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
                if ($null -ne $statusElement) {
                    # TextBlock's explicit automation name is stable; inspect the accessible text pattern.
                    $pattern = $null
                    if ($statusElement.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
                        $status = $pattern.DocumentRange.GetText(-1)
                    } else {
                        $status = $statusElement.Current.Name
                    }
                }
                $gridCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'OriginalGrid')
                $grid = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $gridCondition)
                if ($null -ne $grid -and $grid.Current.IsEnabled) {
                    $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))
                    if ($rows.Count -eq 1 -and $rows[0].Current.Name -match 'Review needed') { break }
                }
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $rows -or $rows.Count -ne 1 -or $rows[0].Current.Name -notmatch 'Review needed') {
            throw "Saved review did not restore correctly. Status: $status"
        }
        $apply = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'RenameButton'))
        if ($apply.Current.IsEnabled) { throw 'A restored batch incorrectly allowed applying before fresh review.' }
        if ($attempt -eq 2) {
            $clear = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ClearButton'))
            ([System.Windows.Automation.InvokePattern]$clear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            $restore = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'RestoreClearedBatchButton'))
            if ($null -eq $restore -or -not $restore.Current.IsEnabled) { throw 'Clearing the batch did not offer restoration.' }
            ([System.Windows.Automation.InvokePattern]$restore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
            $restoreDeadline = (Get-Date).AddSeconds(10)
            do {
                Start-Sleep -Milliseconds 200
                $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))
            } while ((Get-Date) -lt $restoreDeadline -and ($rows.Count -ne 1 -or -not $grid.Current.IsEnabled))
            if ($rows.Count -ne 1 -or $rows[0].Current.Name -notmatch 'Review needed' -or $apply.Current.IsEnabled) {
                throw 'Restoring the cleared batch did not preserve the fresh-review gate.'
            }
            Write-Host 'Packaged clear/restore workflow passed without changing the source fixture.'
        }
        Start-Sleep -Seconds 3
        if (-not $process.CloseMainWindow()) { throw 'Could not request normal window closure.' }
        if (-not $process.WaitForExit(10000)) { throw 'The app did not close normally.' }
        $saved = Get-Content -Raw -LiteralPath (Join-Path $runDirectory 'workspace.json') | ConvertFrom-Json
        if ($saved.Items.Count -ne 1 -or $saved.Items[0].MatchedTitle -ne 'Edited Example') {
            throw 'Normal closure did not preserve the edited review and drop the missing source.'
        }
        if ($saved.Items[0].DestinationPath -eq (Join-Path $runDirectory 'obsolete.mkv')) { throw 'A saved destination was reused without replanning.' }
        if (-not (Test-Path -LiteralPath $source)) { throw 'Restoration changed the original fixture.' }
        Write-Host "Packaged workspace restore/save cycle $attempt passed."
    }
    finally {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}
Write-Host "Workspace evidence: $runDirectory"
