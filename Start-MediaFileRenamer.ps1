$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:APPDATA = Join-Path $root '.appdata'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'

New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

& (Join-Path $root '.dotnet\dotnet.exe') run --project (Join-Path $root 'src\MediaFileRenamer.App\MediaFileRenamer.App.csproj')
