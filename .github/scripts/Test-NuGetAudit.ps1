[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SolutionPath
)

$ErrorActionPreference = 'Stop'

function Invoke-PackageAudit {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$FailurePattern,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "The NuGet audit command failed with exit code $LASTEXITCODE."
    }

    $text = $output -join [Environment]::NewLine
    Write-Host $text
    if ($text -match $FailurePattern) {
        throw $FailureMessage
    }
}

Invoke-PackageAudit `
    -Arguments @('list', $SolutionPath, 'package', '--vulnerable', '--include-transitive') `
    -FailurePattern 'has the following vulnerable packages' `
    -FailureMessage 'The dependency audit found one or more vulnerable NuGet packages.'

Invoke-PackageAudit `
    -Arguments @('list', $SolutionPath, 'package', '--deprecated', '--include-transitive') `
    -FailurePattern 'has the following deprecated packages' `
    -FailureMessage 'The dependency audit found one or more deprecated NuGet packages.'
