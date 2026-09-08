[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath,

    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$publishedDirectory = Split-Path -Parent $resolvedExecutable
$runRoot = Join-Path $env:TEMP ("MediaFileRenamer-update-handoff-" + [Guid]::NewGuid().ToString('N'))
$installationDirectory = Join-Path $runRoot 'installed'
$sessionDirectory = Join-Path $runRoot 'session'
$helperDirectory = Join-Path $sessionDirectory 'update-helper'
$dataRoot = Join-Path $runRoot 'app-data'
$helper = $null
$startedApplication = $null

function Wait-ForFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [datetime]$Deadline
    )

    while ((Get-Date) -lt $Deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for '$Path'."
}

try {
    New-Item -ItemType Directory -Path $installationDirectory, $helperDirectory, $dataRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $publishedDirectory '*') -Destination $installationDirectory -Recurse -Force
    Copy-Item -Path (Join-Path $installationDirectory '*') -Destination $helperDirectory -Recurse -Force

    # The helper must restore this intentionally stale runtime file from the update ZIP.
    $runtimeAssembly = Join-Path $installationDirectory 'MediaFileRenamer.dll'
    if (-not (Test-Path -LiteralPath $runtimeAssembly)) {
        throw 'The published application did not contain MediaFileRenamer.dll.'
    }

    [System.IO.File]::AppendAllText($runtimeAssembly, 'stale-update-test-marker')

    New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
    $packagePath = Join-Path $sessionDirectory 'MediaFileRenamer-win-x64.zip'
    Compress-Archive -Path (Join-Path $publishedDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    $planPath = Join-Path $sessionDirectory 'update-plan.json'
    [ordered]@{
        ParentProcessId = 0
        InstallationDirectory = $installationDirectory
        PackagePath = $packagePath
        ExpectedSha256 = $checksum
        RelaunchArguments = @('--data-root', $dataRoot)
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $planPath -NoNewline

    $helperPath = Join-Path $helperDirectory 'MediaFileRenamer.exe'
    $helper = Start-Process -FilePath $helperPath -ArgumentList @('--apply-update-plan', $planPath) -PassThru
    if (-not $helper.WaitForExit($TimeoutSeconds * 1000)) {
        throw "The update helper did not exit within $TimeoutSeconds seconds."
    }

    if ($helper.ExitCode -ne 0) {
        throw "The update helper exited with code $($helper.ExitCode)."
    }

    $statusPath = Join-Path $sessionDirectory 'update-status.json'
    Wait-ForFile -Path $statusPath -Deadline (Get-Date).AddSeconds(10)
    $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if (-not $status.IsTerminal -or -not $status.Succeeded) {
        throw "The update did not report success. Stage: $($status.Stage). Message: $($status.Message)"
    }

    $publishedHash = (Get-FileHash -LiteralPath (Join-Path $publishedDirectory 'MediaFileRenamer.dll') -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath $runtimeAssembly -Algorithm SHA256).Hash
    if ($installedHash -ne $publishedHash) {
        throw 'The update helper did not replace the stale runtime file.'
    }

    $applicationLog = Join-Path $dataRoot 'Logs\MediaFileRenamer.log'
    Wait-ForFile -Path $applicationLog -Deadline (Get-Date).AddSeconds(10)
    $startedApplication = Get-Process -Name MediaFileRenamer -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $installationDirectory 'MediaFileRenamer.exe') } |
        Select-Object -First 1
    if ($null -eq $startedApplication) {
        throw 'The updated application did not remain running after the helper exited.'
    }

    Write-Host 'Portable update handoff acceptance test passed.'
}
finally {
    if ($null -ne $helper -and -not $helper.HasExited) {
        Stop-Process -Id $helper.Id -Force
        $helper.WaitForExit()
    }

    if ($null -ne $startedApplication -and -not $startedApplication.HasExited) {
        Stop-Process -Id $startedApplication.Id -Force
        $startedApplication.WaitForExit()
    }

    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
