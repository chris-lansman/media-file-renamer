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
    [string] $DataRoot,

    [Parameter(Mandatory)]
    [string] $ArtifactRoot,

    [Parameter(Mandatory)]
    [ValidateRange(48, 960)]
    [int] $ExpectedDpi,

    [Parameter(Mandatory)]
    [ValidateSet("Enabled", "Disabled")]
    [string] $ExpectedHighContrast,

    [ValidateSet(100, 200)]
    [int] $ExpectedTextScalePercent = 100,

    [ValidateRange(5, 120)]
    [int] $TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName PresentationFramework

$script:Checks = New-Object System.Collections.ArrayList
$script:TargetProcess = $null
$script:ExpectedHighContrastValue = $ExpectedHighContrast -eq "Enabled"
$script:SensitiveAutomationIds = @(
    "TmdbApiKeyBox",
    "TvdbApiKeyBox",
    "TvdbPinBox"
)

function Add-Check {
    param(
        [Parameter(Mandatory)]
        [string] $Id,

        [Parameter(Mandatory)]
        [bool] $Passed,

        [Parameter(Mandatory)]
        [string] $Message,

        [object] $Evidence
    )

    [void]$script:Checks.Add([pscustomobject][ordered]@{
        id = $Id
        passed = $Passed
        message = $Message
        evidence = $Evidence
    })
}

function Resolve-SafeFreshRoot {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Purpose
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    $isDriveAbsolute = $expanded -match '^[A-Za-z]:[\\/]'
    $isUnc = $expanded -match '^\\\\[^\\]+\\[^\\]+(?:\\|$)'
    if (-not ($isDriveAbsolute -or $isUnc)) {
        throw "$Purpose must be an absolute Windows path."
    }

    $fullPath = [IO.Path]::GetFullPath($expanded)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.TrimEnd('\', '/') -eq $pathRoot.TrimEnd('\', '/')) {
        throw "$Purpose must not be a drive or share root: '$fullPath'."
    }

    if ([IO.File]::Exists($fullPath)) {
        throw "$Purpose names an existing file: '$fullPath'."
    }

    if ([IO.Directory]::Exists($fullPath)) {
        $entry = Get-ChildItem -LiteralPath $fullPath -Force | Select-Object -First 1
        if ($null -ne $entry) {
            throw "$Purpose must be new or empty: '$fullPath'."
        }
    }

    return $fullPath
}

function Protect-EvidenceValue {
    param(
        [string] $AutomationId,
        [string] $Value
    )

    if ($script:SensitiveAutomationIds -contains $AutomationId) {
        return "[REDACTED]"
    }

    if ([string]::IsNullOrEmpty($Value)) {
        return $Value
    }

    return [regex]::Replace(
        $Value,
        '(?i)(?:[A-Z]:\\|\\\\[^\\\s]+\\)[^"\r\n]+',
        '[PATH]')
}

function Initialize-NativeUiSupport {
    if ($null -eq ("MediaFileRenamerAcceptanceNative" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MediaFileRenamerAcceptanceNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out RECT value,
        int valueSize);
}
'@
    }

    # PER_MONITOR_AWARE_V2. A fresh STA PowerShell process should not yet own UI.
    $dpiContext = [IntPtr]::new(-4)
    $dpiAware = [MediaFileRenamerAcceptanceNative]::SetProcessDpiAwarenessContext(
        $dpiContext)
    $dpiAwarenessError =
        [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Add-Check `
        -Id "environment.dpi-awareness" `
        -Passed ($dpiAware -or $dpiAwarenessError -eq 5) `
        -Message "Acceptance process requested per-monitor-v2 DPI awareness or was already DPI initialized." `
        -Evidence @{
            changed = $dpiAware
            win32Error = $dpiAwarenessError
        }
}

function Convert-Rectangle {
    param([System.Windows.Rect] $Rectangle)

    return [ordered]@{
        x = $Rectangle.X
        y = $Rectangle.Y
        width = $Rectangle.Width
        height = $Rectangle.Height
        right = $Rectangle.Right
        bottom = $Rectangle.Bottom
    }
}

function Convert-NativeRectangle {
    param($Rectangle)

    return [ordered]@{
        x = $Rectangle.Left
        y = $Rectangle.Top
        width = $Rectangle.Right - $Rectangle.Left
        height = $Rectangle.Bottom - $Rectangle.Top
        right = $Rectangle.Right
        bottom = $Rectangle.Bottom
    }
}

function Get-WindowEnvironment {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $nativeRect = New-Object MediaFileRenamerAcceptanceNative+RECT
    $nativeRectResult =
        [MediaFileRenamerAcceptanceNative]::DwmGetWindowAttribute(
            $handle,
            9,
            [ref]$nativeRect,
            [Runtime.InteropServices.Marshal]::SizeOf($nativeRect))
    $monitor = [MediaFileRenamerAcceptanceNative]::MonitorFromWindow($handle, 2)
    $monitorInfo = New-Object MediaFileRenamerAcceptanceNative+MONITORINFO
    $monitorInfo.cbSize =
        [Runtime.InteropServices.Marshal]::SizeOf($monitorInfo)
    $monitorResult =
        [MediaFileRenamerAcceptanceNative]::GetMonitorInfo(
            $monitor,
            [ref]$monitorInfo)

    return [ordered]@{
        handle = $handle.ToInt64()
        dpi = [int][MediaFileRenamerAcceptanceNative]::GetDpiForWindow($handle)
        scalePercent = [Math]::Round(
            [MediaFileRenamerAcceptanceNative]::GetDpiForWindow($handle) / 96.0 * 100,
            0)
        uiaBounds = Convert-Rectangle $Window.Current.BoundingRectangle
        dwmBounds = if ($nativeRectResult -eq 0) {
            Convert-NativeRectangle $nativeRect
        } else {
            $null
        }
        monitor = if ($monitorResult) {
            [ordered]@{
                bounds = Convert-NativeRectangle $monitorInfo.rcMonitor
                workArea = Convert-NativeRectangle $monitorInfo.rcWork
            }
        } else {
            $null
        }
        highContrast = [bool][System.Windows.SystemParameters]::HighContrast
    }
}

function Get-TopLevelWindows {
    param([int] $ProcessId)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
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
        [Parameter(Mandatory)]
        [int] $ProcessId,

        [Parameter(Mandatory)]
        [string] $Name,

        [switch] $Absent
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $match = Get-TopLevelWindows -ProcessId $ProcessId |
            Where-Object { $_.Current.Name -eq $Name } |
            Select-Object -First 1
        if ($Absent) {
            if ($null -eq $match) {
                return $null
            }
        } elseif ($null -ne $match) {
            return $match
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($Absent) {
        throw "Window '$Name' did not close within $TimeoutSeconds seconds."
    }

    throw "Window '$Name' did not appear within $TimeoutSeconds seconds."
}

function Find-UiaElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Root,

        [string] $AutomationId,
        [string] $Name
    )

    if (-not [string]::IsNullOrWhiteSpace($AutomationId)) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId)
    } elseif (-not [string]::IsNullOrWhiteSpace($Name)) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)
    } else {
        throw "Find-UiaElement requires AutomationId or Name."
    }

    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Get-RuntimeIdKey {
    param([System.Windows.Automation.AutomationElement] $Element)

    return (($Element.GetRuntimeId() | ForEach-Object { $_.ToString() }) -join ".")
}

function Get-SupportedPatternNames {
    param([System.Windows.Automation.AutomationElement] $Element)

    return @(
        $Element.GetSupportedPatterns() |
            ForEach-Object { $_.ProgrammaticName } |
            Sort-Object
    )
}

function Get-UiaValue {
    param([System.Windows.Automation.AutomationElement] $Element)

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        return Protect-EvidenceValue `
            -AutomationId $Element.Current.AutomationId `
            -Value $pattern.Current.Value
    }

    return $null
}

function Get-ControlTreeEvidence {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window
    )

    $condition = [System.Windows.Automation.Automation]::ControlViewCondition
    $collection = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $evidence = @()
    for ($index = 0; $index -lt $collection.Count; $index++) {
        $element = $collection[$index]
        try {
            $evidence += [pscustomobject][ordered]@{
                runtimeId = Get-RuntimeIdKey $element
                automationId = $element.Current.AutomationId
                name = Protect-EvidenceValue `
                    -AutomationId $element.Current.AutomationId `
                    -Value $element.Current.Name
                controlType = $element.Current.ControlType.ProgrammaticName
                enabled = $element.Current.IsEnabled
                focusable = $element.Current.IsKeyboardFocusable
                offscreen = $element.Current.IsOffscreen
                bounds = Convert-Rectangle $element.Current.BoundingRectangle
                patterns = Get-SupportedPatternNames $element
                value = Get-UiaValue $element
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            # The UI changed while it was being sampled; the next audit catches it.
        }
    }

    return $evidence
}

function Test-RequiredPattern {
    param(
        [System.Windows.Automation.AutomationElement] $Element,
        [string[]] $Patterns
    )

    $supported = Get-SupportedPatternNames $Element
    foreach ($pattern in $Patterns) {
        if ($supported -contains $pattern) {
            return $true
        }
    }

    return $false
}

function Get-RequiredPatternsForControl {
    param([System.Windows.Automation.ControlType] $ControlType)

    if ($ControlType -eq [System.Windows.Automation.ControlType]::Button) {
        return @("InvokePatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::Edit) {
        return @("ValuePatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::CheckBox) {
        return @("TogglePatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::ComboBox) {
        return @(
            "ExpandCollapsePatternIdentifiers.Pattern",
            "SelectionPatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::List) {
        return @("SelectionPatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::DataGrid) {
        return @(
            "GridPatternIdentifiers.Pattern",
            "SelectionPatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::Hyperlink) {
        return @("InvokePatternIdentifiers.Pattern")
    }
    if ($ControlType -eq [System.Windows.Automation.ControlType]::MenuItem) {
        return @(
            "InvokePatternIdentifiers.Pattern",
            "ExpandCollapsePatternIdentifiers.Pattern")
    }

    return @()
}

function Test-HasScrollableAncestor {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window
    )

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $current = $walker.GetParent($Element)
    while ($null -ne $current) {
        if ((Get-RuntimeIdKey $current) -eq (Get-RuntimeIdKey $Window)) {
            return $false
        }

        try {
            $pattern = $null
            if ($current.TryGetCurrentPattern(
                    [System.Windows.Automation.ScrollPattern]::Pattern,
                    [ref]$pattern)) {
                $scroll = [System.Windows.Automation.ScrollPattern]$pattern
                if ($scroll.Current.VerticallyScrollable `
                    -or $scroll.Current.HorizontallyScrollable) {
                    return $true
                }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            return $false
        }

        $current = $walker.GetParent($current)
    }

    return $false
}

function Audit-Window {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window,

        [Parameter(Mandatory)]
        [string] $Slug
    )

    $environment = Get-WindowEnvironment $Window
    $all = $Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Automation]::ControlViewCondition)
    $focusable = @()
    $failures = @()
    $windowBounds = $Window.Current.BoundingRectangle

    for ($index = 0; $index -lt $all.Count; $index++) {
        $element = $all[$index]
        try {
            if (-not $element.Current.IsEnabled `
                -or -not $element.Current.IsKeyboardFocusable `
                -or $element.Current.IsOffscreen) {
                continue
            }

            $focusable += $element
            $identity = [ordered]@{
                automationId = $element.Current.AutomationId
                name = $element.Current.Name
                controlType = $element.Current.ControlType.ProgrammaticName
            }
            if ([string]::IsNullOrWhiteSpace($element.Current.Name)) {
                $failures += [pscustomobject]@{
                    reason = "Focusable control has no accessible name."
                    element = $identity
                }
            }

            $bounds = $element.Current.BoundingRectangle
            $inside = $bounds.Width -gt 0 -and $bounds.Height -gt 0 `
                -and $bounds.Left -ge ($windowBounds.Left - 2) `
                -and $bounds.Top -ge ($windowBounds.Top - 2) `
                -and $bounds.Right -le ($windowBounds.Right + 2) `
                -and $bounds.Bottom -le ($windowBounds.Bottom + 2)
            if (-not $inside `
                -and -not (Test-HasScrollableAncestor `
                    -Element $element `
                    -Window $Window)) {
                $failures += [pscustomobject]@{
                    reason = "Focusable control is clipped or outside its window."
                    element = $identity
                    bounds = Convert-Rectangle $bounds
                }
            }

            $requiredPatterns = @(
                Get-RequiredPatternsForControl $element.Current.ControlType
            )
            if ($requiredPatterns.Count -gt 0 `
                -and -not (Test-RequiredPattern $element $requiredPatterns)) {
                $failures += [pscustomobject]@{
                    reason = "Focusable control lacks its required UIA pattern."
                    element = $identity
                    expectedAny = $requiredPatterns
                    actual = Get-SupportedPatternNames $element
                }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] {
            $failures += [pscustomobject]@{
                reason = "Control disappeared during accessibility inspection."
            }
        }
    }

    $tree = Get-ControlTreeEvidence $Window
    $treePath = Join-Path $ArtifactRoot "$Slug-tree.json"
    [IO.File]::WriteAllText(
        $treePath,
        ($tree | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    $windowPath = Join-Path $ArtifactRoot "$Slug-window.json"
    [IO.File]::WriteAllText(
        $windowPath,
        ($environment | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    Add-Check `
        -Id "$Slug.accessibility" `
        -Passed ($failures.Count -eq 0) `
        -Message "Every visible enabled focusable control has a name, bounds, and role-appropriate UIA pattern." `
        -Evidence @{
            focusableCount = $focusable.Count
            failures = $failures
            tree = $treePath
            window = $windowPath
        }

    Add-Check `
        -Id "$Slug.dpi" `
        -Passed ($environment.dpi -eq $ExpectedDpi) `
        -Message "Window DPI matches the requested acceptance environment." `
        -Evidence @{
            expected = $ExpectedDpi
            actual = $environment.dpi
            scalePercent = $environment.scalePercent
        }
    Add-Check `
        -Id "$Slug.high-contrast" `
        -Passed ($environment.highContrast -eq $script:ExpectedHighContrastValue) `
        -Message "High Contrast state matches the requested acceptance environment." `
        -Evidence @{
            expected = $script:ExpectedHighContrastValue
            actual = $environment.highContrast
        }

    return ,$focusable
}

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window,

        [Parameter(Mandatory)]
        [string] $Slug
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    [void][MediaFileRenamerAcceptanceNative]::ShowWindow($handle, 9)
    [void][MediaFileRenamerAcceptanceNative]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 250

    $bounds = $Window.Current.BoundingRectangle
    $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $left = [Math]::Max([int][Math]::Floor($bounds.Left), $virtual.Left)
    $top = [Math]::Max([int][Math]::Floor($bounds.Top), $virtual.Top)
    $right = [Math]::Min([int][Math]::Ceiling($bounds.Right), $virtual.Right)
    $bottom = [Math]::Min([int][Math]::Ceiling($bounds.Bottom), $virtual.Bottom)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -le 0 -or $height -le 0) {
        Add-Check `
            -Id "$Slug.screenshot" `
            -Passed $false `
            -Message "Window has no capturable on-screen area." `
            -Evidence (Convert-Rectangle $bounds)
        return
    }

    $path = Join-Path $ArtifactRoot "$Slug.png"
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $left,
            $top,
            0,
            0,
            (New-Object System.Drawing.Size($width, $height)))
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Add-Check `
            -Id "$Slug.screenshot" `
            -Passed $true `
            -Message "Captured the visible packaged window." `
            -Evidence $path
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Test-TabTraversal {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window,

        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement[]] $Focusable,

        [Parameter(Mandatory)]
        [string] $Slug
    )

    $tabControlTypes = @(
        [System.Windows.Automation.ControlType]::Button,
        [System.Windows.Automation.ControlType]::Edit,
        [System.Windows.Automation.ControlType]::CheckBox,
        [System.Windows.Automation.ControlType]::ComboBox,
        [System.Windows.Automation.ControlType]::List,
        [System.Windows.Automation.ControlType]::DataGrid,
        [System.Windows.Automation.ControlType]::Hyperlink,
        [System.Windows.Automation.ControlType]::TabItem
    )
    $expected = @(
        $Focusable | Where-Object {
            $tabControlTypes -contains $_.Current.ControlType
        }
    )
    if ($expected.Count -eq 0) {
        Add-Check `
            -Id "$Slug.tab-order" `
            -Passed $false `
            -Message "No tab-focusable controls were discovered." `
            -Evidence $null
        return
    }

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    [void][MediaFileRenamerAcceptanceNative]::SetForegroundWindow($handle)
    $expected[0].SetFocus()
    Start-Sleep -Milliseconds 100

    $forward = New-Object System.Collections.ArrayList
    $initialFocus =
        [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($null -eq $initialFocus `
        -or $initialFocus.Current.ProcessId -ne $script:TargetProcess.Id) {
        Add-Check `
            -Id "$Slug.tab-order" `
            -Passed $false `
            -Message "Could not establish keyboard focus inside the target window." `
            -Evidence $null
        return
    }
    $firstKey = Get-RuntimeIdKey $initialFocus
    $forwardEscaped = $false
    for ($index = 0; $index -lt ($expected.Count * 2 + 8); $index++) {
        $focused =
            [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $focused `
            -or $focused.Current.ProcessId -ne $script:TargetProcess.Id) {
            $forwardEscaped = $true
            break
        }

        $key = Get-RuntimeIdKey $focused
        if ($index -gt 0 -and $key -eq $firstKey) {
            break
        }

        [void]$forward.Add([pscustomobject][ordered]@{
            runtimeId = $key
            automationId = $focused.Current.AutomationId
            name = $focused.Current.Name
            controlType = $focused.Current.ControlType.ProgrammaticName
        })
        [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
        Start-Sleep -Milliseconds 80
    }

    $expectedKeys = @($expected | ForEach-Object { Get-RuntimeIdKey $_ })
    $forwardKeys = @($forward | ForEach-Object { $_.runtimeId })

    $expected[0].SetFocus()
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait("+{TAB}")
    Start-Sleep -Milliseconds 80
    $reverse = New-Object System.Collections.ArrayList
    $reverseFirst =
        [System.Windows.Automation.AutomationElement]::FocusedElement
    $reverseFirstKey = if ($null -eq $reverseFirst) {
        ""
    } else {
        Get-RuntimeIdKey $reverseFirst
    }
    $reverseEscaped = $false
    for ($index = 0; $index -lt ($expected.Count * 2 + 8); $index++) {
        $focused =
            [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $focused `
            -or $focused.Current.ProcessId -ne $script:TargetProcess.Id) {
            $reverseEscaped = $true
            break
        }

        $key = Get-RuntimeIdKey $focused
        if ($index -gt 0 -and $key -eq $reverseFirstKey) {
            break
        }

        [void]$reverse.Add([pscustomobject][ordered]@{
            runtimeId = $key
            automationId = $focused.Current.AutomationId
            name = $focused.Current.Name
            controlType = $focused.Current.ControlType.ProgrammaticName
        })
        [System.Windows.Forms.SendKeys]::SendWait("+{TAB}")
        Start-Sleep -Milliseconds 80
    }

    $reverseKeys = @($reverse | ForEach-Object { $_.runtimeId })
    $containerControlTypes = @(
        "ControlType.DataGrid",
        "ControlType.List"
    )
    $forwardRingKeys = @(
        $forward |
            Where-Object {
                $containerControlTypes -notcontains $_.controlType
            } |
            ForEach-Object { $_.runtimeId }
    )
    $reverseRingKeys = @(
        $reverse |
            Where-Object {
                $containerControlTypes -notcontains $_.controlType
            } |
            ForEach-Object { $_.runtimeId }
    )
    $forwardUnique = @(
        $forwardRingKeys | Select-Object -Unique | Sort-Object)
    $reverseUnique = @(
        $reverseRingKeys | Select-Object -Unique | Sort-Object)
    $sameRing =
        @(Compare-Object $forwardUnique $reverseUnique).Count -eq 0
    $unnamedVisited = @(
        @($forward) + @($reverse) |
            Where-Object { [string]::IsNullOrWhiteSpace($_.name) }
    )
    $notObserved = @(
        $expectedKeys |
            Where-Object {
                $forwardKeys -notcontains $_ -and $reverseKeys -notcontains $_
            }
    )
    $evidencePath = Join-Path $ArtifactRoot "$Slug-focus.json"
    $focusEvidence = [ordered]@{
        discoveredCandidateCount = $expected.Count
        forward = @($forward)
        reverse = @($reverse)
        focusEscapedForward = $forwardEscaped
        focusEscapedReverse = $reverseEscaped
        forwardAndReverseVisitSameRing = $sameRing
        unnamedVisitedCount = $unnamedVisited.Count
        candidatesNotObservedRuntimeIds = $notObserved
    }
    [IO.File]::WriteAllText(
        $evidencePath,
        ($focusEvidence | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    Add-Check `
        -Id "$Slug.tab-order" `
        -Passed (
            $forward.Count -gt 0 `
            -and $reverse.Count -gt 0 `
            -and -not $forwardEscaped `
            -and -not $reverseEscaped `
            -and $sameRing `
            -and $unnamedVisited.Count -eq 0) `
        -Message "Forward and reverse Tab traversal stay in the app, visit the same focus ring, and expose named controls." `
        -Evidence $evidencePath
}

function Invoke-UiaElement {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        throw "Element '$($Element.Current.Name)' does not support InvokePattern."
    }
    $pattern.Invoke()
}

function Set-UiaValue {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element,

        [Parameter(Mandatory)]
        [string] $Value
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        throw "Element '$($Element.Current.AutomationId)' does not support ValuePattern."
    }
    $pattern.SetValue($Value)
}

function Get-UiaText {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element
    )

    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern,
            [ref]$pattern)) {
        return $pattern.DocumentRange.GetText(-1).Trim()
    }

    return $Element.Current.Name
}

function Wait-UiaTextContains {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Element,

        [Parameter(Mandatory)]
        [string] $Expected
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $text = Get-UiaText -Element $Element
        if ($text.Contains($Expected)) {
            return $text
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Element '$($Element.Current.AutomationId)' did not report text containing '$Expected'."
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

function Open-SyntheticFixture {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $MainWindow,

        [Parameter(Mandatory)]
        [string] $FixturePath
    )

    Invoke-UiaElement (
        Find-UiaElement -Root $MainWindow -AutomationId "AddFilesButton")
    $dialog = Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name "Open"
    $fileName = Find-UiaElement -Root $dialog -AutomationId "1148"
    if ($null -eq $fileName) {
        throw "The native Open dialog did not expose its file-name control."
    }

    $valueTarget = $fileName.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit))
    if ($null -eq $valueTarget) {
        $valueTarget = $fileName
    }
    if ($null -eq $valueTarget) {
        throw "The native Open dialog file-name field is not editable."
    }

    [void][MediaFileRenamerAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$dialog.Current.NativeWindowHandle)
    $valueTarget.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    [System.Windows.Forms.SendKeys]::SendWait($FixturePath)
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
    if ($null -eq $open) {
        $open = Find-UiaElement -Root $dialog -Name "Open"
    }
    if ($null -eq $open) {
        throw "The native Open dialog did not expose its Open button."
    }
    Invoke-UiaElement $open
    Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name "Open" `
        -Absent | Out-Null
}

function Open-ManualChoiceForFirstRow {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $MainWindow
    )

    $grid = Find-UiaElement -Root $MainWindow -AutomationId "OriginalGrid"
    $rows = $grid.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem))
    if ($rows.Count -lt 1) {
        throw "The synthetic media file did not appear in the Original Files grid."
    }

    $selection = $null
    if (-not $rows[0].TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$selection)) {
        throw "The first Original Files row is not selectable through UI Automation."
    }
    $selection.Select()

    $choose = Find-UiaElement `
        -Root $MainWindow `
        -AutomationId "ChooseSelectedButton"
    Wait-UiaElementEnabled -Element $choose
    Invoke-UiaElement $choose
    return Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name "Classify Media"
}

function Close-WithEscape {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Slug,

        [string] $FallbackCloseName
    )

    [void][MediaFileRenamerAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$Window.Current.NativeWindowHandle)
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    $closed = $true
    try {
        Wait-UiaWindow `
            -ProcessId $script:TargetProcess.Id `
            -Name $Name `
            -Absent | Out-Null
    } catch {
        $closed = $false
    }

    Add-Check `
        -Id "$Slug.escape" `
        -Passed $closed `
        -Message "Escape closes the cancelable window." `
        -Evidence @{ window = $Name }

    if (-not $closed -and -not [string]::IsNullOrWhiteSpace($FallbackCloseName)) {
        $close = Find-UiaElement -Root $Window -Name $FallbackCloseName
        if ($null -ne $close) {
            Invoke-UiaElement $close
            Wait-UiaWindow `
                -ProcessId $script:TargetProcess.Id `
                -Name $Name `
                -Absent | Out-Null
        }
    }
}

function Open-KeyboardMenuWindow {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $MainWindow,

        [Parameter(Mandatory)]
        [string] $MenuKey,

        [Parameter(Mandatory)]
        [string] $ItemKey,

        [Parameter(Mandatory)]
        [string] $WindowName,

        [Parameter(Mandatory)]
        [string] $Slug
    )

    [void][MediaFileRenamerAcceptanceNative]::SetForegroundWindow(
        [IntPtr]$MainWindow.Current.NativeWindowHandle)
    [System.Windows.Forms.SendKeys]::SendWait("%$MenuKey")
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait($ItemKey)
    $window = Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name $WindowName
    Add-Check `
        -Id "$Slug.menu-path" `
        -Passed ($null -ne $window) `
        -Message "Keyboard menu path opens the expected window." `
        -Evidence @{ menu = "Alt+$MenuKey,$ItemKey"; window = $WindowName }
    return $window
}

function Audit-Capture-Traverse {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement] $Window,

        [Parameter(Mandatory)]
        [string] $Slug
    )

    $focusable = Audit-Window -Window $Window -Slug $Slug
    Save-WindowScreenshot -Window $Window -Slug $Slug
    Test-TabTraversal -Window $Window -Focusable $focusable -Slug $Slug
}

if ([Threading.Thread]::CurrentThread.ApartmentState -ne `
    [Threading.ApartmentState]::STA) {
    throw "Run this harness with Windows PowerShell 5.1 and -STA."
}
if ($PSVersionTable.PSEdition -ne "Desktop" `
    -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw "This harness requires Windows PowerShell 5.1."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$resolvedDataRoot = Resolve-SafeFreshRoot -Path $DataRoot -Purpose "DataRoot"
$resolvedArtifactRoot =
    Resolve-SafeFreshRoot -Path $ArtifactRoot -Purpose "ArtifactRoot"
if ($resolvedDataRoot.StartsWith(
        $resolvedArtifactRoot + "\",
        [StringComparison]::OrdinalIgnoreCase) `
    -or $resolvedArtifactRoot.StartsWith(
        $resolvedDataRoot + "\",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "DataRoot and ArtifactRoot must not contain one another."
}

$hash = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash
if ($hash -ne $ExpectedSha256.ToUpperInvariant()) {
    throw "Executable SHA-256 mismatch. Expected $ExpectedSha256, actual $hash."
}
$version = (Get-Item -LiteralPath $resolvedExecutable).VersionInfo.ProductVersion
if ($version -ne $ExpectedProductVersion) {
    throw "Executable product version mismatch. Expected '$ExpectedProductVersion', actual '$version'."
}

$running = @(Get-Process -Name "MediaFileRenamer" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Another MediaFileRenamer process is running. Close it before acceptance."
}

[IO.Directory]::CreateDirectory($resolvedDataRoot) | Out-Null
[IO.Directory]::CreateDirectory($resolvedArtifactRoot) | Out-Null
$runId = [Guid]::NewGuid().ToString("N")
$marker = [ordered]@{
    schemaVersion = 1
    purpose = "Media File Renamer isolated UI acceptance data"
    runId = $runId
    executable = $resolvedExecutable
    sha256 = $hash
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
[IO.File]::WriteAllText(
    (Join-Path $resolvedDataRoot ".media-file-renamer-acceptance.json"),
    ($marker | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))

Initialize-NativeUiSupport

$textScale = Get-ItemProperty `
    -LiteralPath "HKCU:\Software\Microsoft\Accessibility" `
    -Name "TextScaleFactor" `
    -ErrorAction SilentlyContinue
$actualTextScalePercent = if ($null -eq $textScale) {
    100
} else {
    [int]$textScale.TextScaleFactor
}
if ($actualTextScalePercent -ne $ExpectedTextScalePercent) {
    throw "Expected Windows text size $ExpectedTextScalePercent%, but found $actualTextScalePercent%."
}
$environmentEvidence = [ordered]@{
    schemaVersion = 1
    runId = $runId
    executable = [ordered]@{
        path = $resolvedExecutable
        sha256 = $hash
        productVersion = $version
        fileVersion =
            (Get-Item -LiteralPath $resolvedExecutable).VersionInfo.FileVersion
    }
    dataRoot = $resolvedDataRoot
    artifactRoot = $resolvedArtifactRoot
    operatingSystem = [Environment]::OSVersion.VersionString
    powershell = $PSVersionTable.PSVersion.ToString()
    apartmentState =
        [Threading.Thread]::CurrentThread.ApartmentState.ToString()
    expectedDpi = $ExpectedDpi
    expectedHighContrast = $script:ExpectedHighContrastValue
    textScalePercent = $actualTextScalePercent
}
[IO.File]::WriteAllText(
    (Join-Path $resolvedArtifactRoot "environment.json"),
    ($environmentEvidence | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

try {
    $argumentList = @(
        "--data-root",
        ('"{0}"' -f $resolvedDataRoot)
    )
    $script:TargetProcess = Start-Process `
        -FilePath $resolvedExecutable `
        -ArgumentList $argumentList `
        -PassThru

    $settings = Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name "Media File Renamer settings"
    $tmdb = Find-UiaElement -Root $settings -AutomationId "TmdbApiKeyBox"
    $tvdb = Find-UiaElement -Root $settings -AutomationId "TvdbApiKeyBox"
    $pin = Find-UiaElement -Root $settings -AutomationId "TvdbPinBox"
    $tmdbStatus = Find-UiaElement `
        -Root $settings `
        -AutomationId "TmdbTestStatusTextBlock"
    $tvdbStatus = Find-UiaElement `
        -Root $settings `
        -AutomationId "TvdbTestStatusTextBlock"
    Invoke-UiaElement (
        Find-UiaElement -Root $settings -AutomationId "TestTmdbButton")
    $tmdbMissingText = Wait-UiaTextContains `
        -Element $tmdbStatus `
        -Expected "Enter a TMDB API key"
    Add-Check `
        -Id "credentials.empty-tmdb" `
        -Passed $true `
        -Message "Packaged TMDB test rejects an empty credential with an actionable local message." `
        -Evidence $tmdbMissingText
    Invoke-UiaElement (
        Find-UiaElement -Root $settings -AutomationId "TestTvdbButton")
    $tvdbMissingText = Wait-UiaTextContains `
        -Element $tvdbStatus `
        -Expected "Enter a TVDB API key"
    Add-Check `
        -Id "credentials.empty-tvdb" `
        -Passed $true `
        -Message "Packaged TVDB test rejects an empty credential with an actionable local message." `
        -Evidence $tvdbMissingText

    $dummySecret = "UIA-SECRET-$runId"
    Set-UiaValue -Element $tmdb -Value $dummySecret
    Set-UiaValue -Element $tvdb -Value "$dummySecret-TVDB"
    Set-UiaValue -Element $pin -Value "$dummySecret-PIN"
    Audit-Capture-Traverse -Window $settings -Slug "first-run-settings"

    $treeText = Get-Content `
        -LiteralPath (Join-Path $resolvedArtifactRoot "first-run-settings-tree.json") `
        -Raw
    Add-Check `
        -Id "first-run-settings.credential-redaction" `
        -Passed (-not $treeText.Contains($dummySecret)) `
        -Message "Credential values are redacted from UIA evidence." `
        -Evidence $null
    Close-WithEscape `
        -Window $settings `
        -Name "Media File Renamer settings" `
        -Slug "first-run-settings" `
        -FallbackCloseName "Cancel settings"

    $main = Wait-UiaWindow `
        -ProcessId $script:TargetProcess.Id `
        -Name "Media File Renamer"
    Audit-Capture-Traverse -Window $main -Slug "main"

    $fixturePath = Join-Path `
        $resolvedArtifactRoot `
        "Synthetic.Movie.2024.mkv"
    [IO.File]::WriteAllBytes($fixturePath, [byte[]](0..63))
    Open-SyntheticFixture `
        -MainWindow $main `
        -FixturePath $fixturePath
    $picker = Open-ManualChoiceForFirstRow -MainWindow $main
    Audit-Capture-Traverse -Window $picker -Slug "local-classification"
    Close-WithEscape `
        -Window $picker `
        -Name "Classify Media" `
        -Slug "local-classification" `
        -FallbackCloseName "Cancel match selection"
    Add-Check `
        -Id "local-classification.synthetic-file" `
        -Passed $true `
        -Message "A synthetic media file reached the packaged local classification choice without provider credentials." `
        -Evidence $fixturePath
    Invoke-UiaElement (
        Find-UiaElement -Root $main -AutomationId "ClearButton")

    $settings = Open-KeyboardMenuWindow `
        -MainWindow $main `
        -MenuKey "f" `
        -ItemKey "s" `
        -WindowName "Media File Renamer settings" `
        -Slug "settings"
    Audit-Capture-Traverse -Window $settings -Slug "settings"
    Close-WithEscape `
        -Window $settings `
        -Name "Media File Renamer settings" `
        -Slug "settings" `
        -FallbackCloseName "Cancel settings"

    $history = Open-KeyboardMenuWindow `
        -MainWindow $main `
        -MenuKey "f" `
        -ItemKey "o" `
        -WindowName "Operation History" `
        -Slug "history"
    Audit-Capture-Traverse -Window $history -Slug "history"
    Close-WithEscape `
        -Window $history `
        -Name "Operation History" `
        -Slug "history" `
        -FallbackCloseName "Close operation history"

    $recovery = Open-KeyboardMenuWindow `
        -MainWindow $main `
        -MenuKey "f" `
        -ItemKey "r" `
        -WindowName "Interrupted operation recovery center" `
        -Slug "recovery"
    Audit-Capture-Traverse -Window $recovery -Slug "recovery"
    Close-WithEscape `
        -Window $recovery `
        -Name "Interrupted operation recovery center" `
        -Slug "recovery" `
        -FallbackCloseName "Close recovery center without changes"

    $about = Open-KeyboardMenuWindow `
        -MainWindow $main `
        -MenuKey "h" `
        -ItemKey "a" `
        -WindowName "About Media File Renamer" `
        -Slug "about"
    Audit-Capture-Traverse -Window $about -Slug "about"
    Close-WithEscape `
        -Window $about `
        -Name "About Media File Renamer" `
        -Slug "about" `
        -FallbackCloseName "Close About window"

    Add-Check `
        -Id "isolation.settings" `
        -Passed (-not [IO.File]::Exists((Join-Path $resolvedDataRoot "settings.json"))) `
        -Message "Acceptance canceled Settings and did not create saved credentials." `
        -Evidence (Join-Path $resolvedDataRoot "settings.json")
    Add-Check `
        -Id "isolation.logs" `
        -Passed ([IO.File]::Exists(
            (Join-Path $resolvedDataRoot "Logs\MediaFileRenamer.log"))) `
        -Message "Diagnostics were written beneath the isolated data root." `
        -Evidence (Join-Path $resolvedDataRoot "Logs\MediaFileRenamer.log")
} catch {
    Add-Check `
        -Id "harness.unhandled" `
        -Passed $false `
        -Message $_.Exception.Message `
        -Evidence $_.ScriptStackTrace
} finally {
    if ($null -ne $script:TargetProcess) {
        try {
            if (-not $script:TargetProcess.HasExited) {
                [void]$script:TargetProcess.CloseMainWindow()
                if (-not $script:TargetProcess.WaitForExit(3000)) {
                    Stop-Process -Id $script:TargetProcess.Id -Force
                    $script:TargetProcess.WaitForExit()
                }
            }
        } catch {
            Add-Check `
                -Id "harness.process-cleanup" `
                -Passed $false `
                -Message "Could not close the exact acceptance process." `
                -Evidence $_.Exception.Message
        }
    }
}

$failedChecks = @($script:Checks | Where-Object { -not $_.passed })
$summary = [ordered]@{
    schemaVersion = 1
    runId = $runId
    passed = $failedChecks.Count -eq 0
    passedCount = @($script:Checks | Where-Object { $_.passed }).Count
    failedCount = $failedChecks.Count
    checks = @($script:Checks)
}
$resultPath = Join-Path $resolvedArtifactRoot "results.json"
[IO.File]::WriteAllText(
    $resultPath,
    ($summary | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))

Write-Output "UI acceptance evidence: $resolvedArtifactRoot"
Write-Output "Results: $resultPath"
if ($failedChecks.Count -gt 0) {
    $failedChecks | ForEach-Object {
        Write-Error "$($_.id): $($_.message)"
    }
    exit 1
}
