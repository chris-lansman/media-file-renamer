[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("None", "Aquatic")]
    [string] $Theme,

    [Parameter(Mandatory)]
    [ValidateSet("None", "Aquatic", "Unsaved Theme")]
    [string] $ExpectedCurrentTheme,

    [ValidateRange(10, 90)]
    [int] $TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($null -eq ("MediaFileRenamerContrastNative" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MediaFileRenamerContrastNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct HIGHCONTRAST
    {
        public int cbSize;
        public int dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HIGHCONTRAST value,
        uint flags);

    public static bool IsHighContrastEnabled()
    {
        var value = new HIGHCONTRAST();
        value.cbSize = Marshal.SizeOf(typeof(HIGHCONTRAST));
        if (!SystemParametersInfo(0x0042, (uint)value.cbSize, ref value, 0))
        {
            throw new InvalidOperationException(
                "Could not read the Windows High Contrast state.");
        }
        return (value.dwFlags & 0x00000001) != 0;
    }
}
'@
}

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
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw $FailureMessage
}

function Get-SelectedTheme {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $ComboBox
    )
    $selection = $ComboBox.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern).
        Current.GetSelection()
    if ($selection.Count -ne 1) {
        throw "Contrast Themes did not expose one selected theme."
    }
    return $selection[0].Current.Name
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$existingSettings = $root.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Settings"))
if ($null -ne $existingSettings) {
    throw "Close the existing Windows Settings window before changing contrast."
}

$settings = $null
try {
    Start-Process "ms-settings:easeofaccess-highcontrast"
    $settings = Wait-Until `
        -Condition {
            $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    "Settings"))
        } `
        -FailureMessage "Windows Contrast Themes settings did not open."

    $comboCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "Contrast themes"),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ComboBox))
    $combo = Wait-Until `
        -Condition {
            $settings.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $comboCondition)
        } `
        -FailureMessage "The Windows contrast-theme control was not found."
    $current = Get-SelectedTheme $combo
    if ($current -ne $ExpectedCurrentTheme) {
        throw "Expected contrast theme '$ExpectedCurrentTheme', but found '$current'. No change was made."
    }

    if ($current -ne $Theme) {
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
            if ($candidate.Current.Name -ne $Theme) {
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
            throw "Contrast theme '$Theme' was not found."
        }
        $target.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

        $apply = $settings.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                "HighContrastThemeApplyButton"))
        if ($null -eq $apply) {
            throw "The Contrast Themes Apply button was not found."
        }
        Wait-Until `
            -Condition {
                if ($apply.Current.IsEnabled) { return $true }
                return $null
            } `
            -FailureMessage "The Contrast Themes Apply button did not enable." |
            Out-Null
        $apply.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $expectedEnabled = $Theme -ne "None"
        Wait-Until `
            -Condition {
                if ([MediaFileRenamerContrastNative]::
                        IsHighContrastEnabled() -eq $expectedEnabled) {
                    return $true
                }
                return $null
            } `
            -FailureMessage "Windows did not apply contrast theme '$Theme'." |
            Out-Null
    }

    [pscustomobject][ordered]@{
        previousTheme = $current
        currentTheme = Get-SelectedTheme $combo
        highContrastEnabled =
            [MediaFileRenamerContrastNative]::IsHighContrastEnabled()
        changed = $current -ne $Theme
    } | ConvertTo-Json -Compress
} finally {
    if ($null -ne $settings) {
        try {
            $settings.GetCurrentPattern(
                [System.Windows.Automation.WindowPattern]::Pattern).Close()
        } catch {
            # Do not hide the original theme-change error.
        }
    }
}
