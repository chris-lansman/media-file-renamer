[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath,

    [ValidateRange(1, 30)]
    [int]$ObservationSeconds = 5
)

$ErrorActionPreference = 'Stop'
$resolvedPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$versionInfo = (Get-Item -LiteralPath $resolvedPath).VersionInfo
$smokeDataRoot = Join-Path $env:TEMP ('MediaFileRenamer-startup-smoke-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($smokeDataRoot) | Out-Null
@{ SchemaVersion = 2; UseTmdbLookup = $false } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $smokeDataRoot 'settings.json')

if ([string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
    throw "The published executable does not contain product version metadata."
}

$process = $null
try {
    $process = Start-Process -FilePath $resolvedPath -ArgumentList @('--data-root', ('"' + $smokeDataRoot + '"')) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds $ObservationSeconds
    if ($process.HasExited) {
        throw "The published application exited during startup with code $($process.ExitCode)."
    }

    Write-Host "Startup smoke test passed for version $($versionInfo.ProductVersion)."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
}
