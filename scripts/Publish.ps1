param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet publish (Join-Path $root 'HomeNetMonitor.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $root 'artifacts/publish')
if ($LASTEXITCODE) { throw 'Publish failed.' }
