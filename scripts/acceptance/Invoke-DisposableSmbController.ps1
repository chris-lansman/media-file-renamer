[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Controller {
    param(
        [Parameter(Mandatory)][bool] $Condition,
        [Parameter(Mandatory)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-StrictDescendant {
    param(
        [Parameter(Mandatory)][string] $Parent,
        [Parameter(Mandatory)][string] $Child
    )

    $prefix = $Parent.TrimEnd('\') + '\'
    return $Child.StartsWith(
        $prefix,
        [StringComparison]::OrdinalIgnoreCase)
}

function New-Signal {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Content
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read)
    try {
        $writer = [IO.StreamWriter]::new($stream)
        try {
            $writer.Write($Content)
        } finally {
            $writer.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Wait-ForSignal {
    param(
        [Parameter(Mandatory)][string[]] $Paths,
        [int] $TimeoutMinutes = 15
    )

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        foreach ($path in $Paths) {
            if (Test-Path -LiteralPath $path) {
                return $path
            }
        }
        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for one of: $($Paths -join ', ')"
}

function Get-OwnedShare {
    $share = Get-SmbShare -Name $script:shareName -ErrorAction SilentlyContinue
    if ($null -eq $share) {
        return $null
    }

    Assert-Controller (
        $share.Path.Equals(
            $script:sharePath,
            [StringComparison]::OrdinalIgnoreCase)) `
        "Share name collision: the existing path is not acceptance-owned."
    Assert-Controller (
        $share.Description -eq $script:description) `
        "Share name collision: the description is not acceptance-owned."
    return $share
}

function New-OwnedShare {
    Assert-Controller ($null -eq (Get-OwnedShare)) `
        "The disposable share already exists."
    New-SmbShare `
        -Name $script:shareName `
        -Path $script:sharePath `
        -Description $script:description `
        -FullAccess $script:currentUser `
        -CachingMode None `
        -Temporary | Out-Null
    Assert-Controller ($null -ne (Get-OwnedShare)) `
        "The disposable share was not created."
}

function Remove-OwnedShare {
    $share = Get-OwnedShare
    if ($null -ne $share) {
        $share | Remove-SmbShare -Force -Confirm:$false
    }
}

$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
$runRoot = [IO.Path]::GetFullPath([string]$manifest.runRoot)
$script:sharePath = [IO.Path]::GetFullPath([string]$manifest.sharePath)
$script:shareName = [string]$manifest.shareName
$script:currentUser = [string]$manifest.currentUser
$nonce = [string]$manifest.nonce
$script:description = "Media File Renamer acceptance $nonce"
$markerPath = [IO.Path]::GetFullPath([string]$manifest.markerPath)

Assert-Controller (
    $script:shareName -match '^MFRACC-[a-f0-9]{10}$') `
    "The disposable share name has an unsafe format."
Assert-Controller (Test-StrictDescendant $runRoot $script:sharePath) `
    "The share path is outside the marked run root."
Assert-Controller (Test-StrictDescendant $runRoot $resolvedManifest) `
    "The controller manifest is outside its run root."
Assert-Controller (Test-StrictDescendant $runRoot $markerPath) `
    "The marker is outside its run root."
Assert-Controller (Test-Path -LiteralPath $script:sharePath -PathType Container) `
    "The disposable share directory does not exist."
Assert-Controller (
    (Get-Content -LiteralPath $markerPath -Raw) -eq $nonce) `
    "The acceptance marker nonce does not match."
Assert-Controller (
    $script:currentUser -eq
        [Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    "The manifest user does not match the elevated controller user."

$signalNames = @(
    "ready", "interruptRequest", "removed", "restoreRequest",
    "restored", "finishRequest", "finished", "error")
$signals = @{}
foreach ($name in $signalNames) {
    $path = [IO.Path]::GetFullPath([string]$manifest.signals.$name)
    Assert-Controller (Test-StrictDescendant $runRoot $path) `
        "Signal '$name' is outside the marked run root."
    $signals[$name] = $path
}

try {
    New-OwnedShare
    New-Signal $signals.ready $script:shareName

    $next = Wait-ForSignal @(
        $signals.interruptRequest,
        $signals.finishRequest)
    if ($next -eq $signals.interruptRequest) {
        Remove-OwnedShare
        Assert-Controller ($null -eq (Get-OwnedShare)) `
            "The disposable share remained online after removal."
        New-Signal $signals.removed $script:shareName

        Wait-ForSignal @($signals.restoreRequest) | Out-Null
        New-OwnedShare
        New-Signal $signals.restored $script:shareName
        Wait-ForSignal @($signals.finishRequest) | Out-Null
    }

    Remove-OwnedShare
    New-Signal $signals.finished $script:shareName
} catch {
    try {
        Remove-OwnedShare
    } catch {
        # Preserve the original controller error.
    }

    if (-not (Test-Path -LiteralPath $signals.error)) {
        New-Signal $signals.error $_.Exception.Message
    }
    throw
}
