[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(100, 200)]
    [int] $Percent,

    [Parameter(Mandatory)]
    [ValidateSet(100, 200)]
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

function Get-ConfiguredTextScale {
    $settings = Get-ItemProperty `
        -LiteralPath "HKCU:\Software\Microsoft\Accessibility" `
        -Name "TextScaleFactor" `
        -ErrorAction SilentlyContinue
    if ($null -eq $settings) {
        return 100
    }

    return [int]$settings.TextScaleFactor
}

$current = Get-ConfiguredTextScale
if ($current -ne $ExpectedCurrentPercent) {
    throw "Expected the current text size to be $ExpectedCurrentPercent%, but it is $current%. No change was made."
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$existingSettings = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Settings"))
if ($null -ne $existingSettings) {
    throw "Close the existing Windows Settings window before changing text size."
}

$settingsWindow = $null
try {
    Start-Process "ms-settings:easeofaccess-display"
    $settingsWindow = Wait-Until `
        -Condition {
            $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    "Settings"))
        } `
        -FailureMessage "Windows Text Size settings did not open."

    $sliderCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Text size"),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Slider))
    $slider = Wait-Until `
        -Condition {
            $settingsWindow.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $sliderCondition)
        } `
        -FailureMessage "The Windows Text Size slider was not found."

    $range = $slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    if ([int][Math]::Round($range.Current.Value) -ne $current) {
        throw "The Text Size slider and configured value disagree. No change was made."
    }

    if ($current -ne $Percent) {
        $range.SetValue($Percent)

        $applyCondition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty,
                "Apply"),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button))
        $apply = Wait-Until `
            -Condition {
                $settingsWindow.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $applyCondition)
            } `
            -FailureMessage "The Windows Text Size Apply button was not found."
        Wait-Until `
            -Condition {
                if ($apply.Current.IsEnabled) { return $true }
                return $null
            } `
            -FailureMessage "The Windows Text Size Apply button did not enable." |
            Out-Null
        $apply.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        Wait-Until `
            -Condition {
                if ((Get-ConfiguredTextScale) -eq $Percent) {
                    return $true
                }
                return $null
            } `
            -FailureMessage "Windows did not apply the $Percent% text size." |
            Out-Null
    }

    [pscustomobject][ordered]@{
        previousPercent = $current
        currentPercent = Get-ConfiguredTextScale
        changed = $current -ne $Percent
    } | ConvertTo-Json -Compress
} finally {
    if ($null -ne $settingsWindow) {
        try {
            $settingsWindow.GetCurrentPattern(
                [System.Windows.Automation.WindowPattern]::Pattern).Close()
        } catch {
            # Do not hide the original text-size change error.
        }
    }
}
