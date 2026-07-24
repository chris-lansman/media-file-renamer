[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERTIFICATE_BASE64) -or
    [string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD)) {
    throw 'The Windows signing certificate and password secrets are both required.'
}

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$appPath = Join-Path $publishPath 'MediaFileRenamer.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "Published executable not found at '$appPath'."
}

$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter signtool.exe `
    -File `
    -Recurse |
    Where-Object FullName -Match '\\x64\\signtool\.exe$' |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if ($null -eq $signTool) {
    throw 'A 64-bit Windows SDK signtool.exe installation was not found.'
}

$pfxPath = Join-Path $env:RUNNER_TEMP 'media-file-renamer-signing.pfx'
$certificate = $null
try {
    [IO.File]::WriteAllBytes(
        $pfxPath,
        [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERTIFICATE_BASE64)
    )
    $password = ConvertTo-SecureString $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD -AsPlainText -Force
    $certificate = Import-PfxCertificate `
        -FilePath $pfxPath `
        -CertStoreLocation Cert:\CurrentUser\My `
        -Password $password `
        -Exportable:$false

    $timestampUrl = $env:WINDOWS_TIMESTAMP_URL
    if ([string]::IsNullOrWhiteSpace($timestampUrl)) {
        $timestampUrl = 'http://timestamp.digicert.com'
    }

    & $signTool.FullName sign `
        /sha1 $certificate.Thumbprint `
        /s My `
        /fd SHA256 `
        /tr $timestampUrl `
        /td SHA256 `
        $appPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed with exit code $LASTEXITCODE."
    }

    & $signTool.FullName verify /pa /v $appPath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verification failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
}
