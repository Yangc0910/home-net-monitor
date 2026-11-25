#Requires -Version 7.0
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][guid]$DeviceAccessProject,
    [Parameter(Mandatory)][string]$Subscription,
    [Parameter(Mandatory)][string]$OAuthClientFile,
    [Parameter(Mandatory)][string]$SubscriberKeyFile
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    & (Join-Path $PSScriptRoot 'Configure-Nest.ps1') -UseDialog @PSBoundParameters
    Restart-Service HomeNetMonitor
    Start-Sleep -Seconds 8
    $config = Get-Content -LiteralPath (Join-Path $env:ProgramData 'HomeNetMonitor/config.json') -Raw | ConvertFrom-Json
    $port = $config.port ?? 8765
    $live = Invoke-RestMethod "http://127.0.0.1:$port/api/status"
    [void][Windows.Forms.MessageBox]::Show(('Configuration saved. Nest status: ' + $live.nest.status), 'HomeNetMonitor')
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'HomeNetMonitor setup failed')
}
