[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(100, 125, 150, 200)]
    [int] $Percent,

    [Parameter(Mandatory)]
    [ValidateSet(100, 125, 150, 200)]
    [int] $ExpectedCurrentPercent,

    [ValidateRange(10, 60)]
    [int] $TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock] $Condition,
        [Parameter(Mandatory)][string] $FailureMessage
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($null -ne $value) {
            return $value
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    throw $FailureMessage
}

function Get-SelectedScale {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $ComboBox
    )

    $selection = $ComboBox.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern).
        Current.GetSelection()
    if ($selection.Count -ne 1) {
        throw "Windows Display Settings did not expose one selected scale."
    }

    if ($selection[0].Current.Name -notmatch '^(\d+)%') {
        throw "Could not parse the selected scale '$($selection[0].Current.Name)'."
    }

    return [int]$Matches[1]
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$existingSettings = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Settings"))
if ($null -ne $existingSettings) {
    throw "Close the existing Windows Settings window before changing scale."
}

$settings = $null
try {
    Start-Process "ms-settings:display"
    $settings = Wait-Until `
        -Condition {
            $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    "Settings"))
        } `
        -FailureMessage "Windows Display Settings did not open."

    $combo = Wait-Until `
        -Condition {
            $settings.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                    "SystemSettings_Display_Scaling_ItemSizeOverride_ComboBox"))
        } `
        -FailureMessage "The Windows display-scale control was not found."

    $current = Get-SelectedScale $combo
    if ($current -ne $ExpectedCurrentPercent) {
        throw "Expected the current display scale to be $ExpectedCurrentPercent%, but it is $current%. No change was made."
    }

    if ($current -ne $Percent) {
        $combo.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $options = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem))
        $target = $null
        for ($index = 0; $index -lt $options.Count; $index++) {
            $candidate = $options[$index]
            if ($candidate.Current.Name -notmatch "^$Percent%(?:\s|$)") {
                continue
            }

            $pattern = $null
            if ($candidate.TryGetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern,
                    [ref]$pattern)) {
                $target = $candidate
                break
            }
        }
        if ($null -eq $target) {
            throw "The requested $Percent% display-scale option was not found."
        }

        $target.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-Until `
            -Condition {
                if ((Get-SelectedScale $combo) -eq $Percent) {
                    return $true
                }
                return $null
            } `
            -FailureMessage "Windows did not apply the $Percent% display scale." |
            Out-Null
    }

    [pscustomobject][ordered]@{
        previousPercent = $current
        currentPercent = Get-SelectedScale $combo
        changed = $current -ne $Percent
    } | ConvertTo-Json -Compress
} finally {
    if ($null -ne $settings) {
        try {
            $settings.GetCurrentPattern(
                [System.Windows.Automation.WindowPattern]::Pattern).Close()
        } catch {
            # Do not hide the original scale-change error.
        }
    }
}
