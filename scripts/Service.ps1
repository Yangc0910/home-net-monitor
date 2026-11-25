#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][ValidateSet('Install','Upgrade','Backup','Disable','Enable','Uninstall')][string]$Action,
    [string]$Package = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish'),
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
$name = 'HomeNetMonitor'
$root = Join-Path $env:ProgramData $name
$store = Join-Path $root 'data'
$program = Join-Path $env:ProgramFiles $name
$service = Get-CimInstance Win32_Service -Filter "Name='$name'"
function Native($tool, [string[]]$arguments) {
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "$tool returned $LASTEXITCODE." }
}
function Stop-Monitor {
    Stop-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
function Start-Monitor {
    Start-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}
function Wait-Monitor($after) {
    $port = (Get-Content -LiteralPath (Join-Path $root 'config.json') -Raw | ConvertFrom-Json).port
    $until = (Get-Date).AddSeconds(30)
    do {
        $now = Get-CimInstance Win32_Service -Filter "Name='$name'"
        try {
            $live = Invoke-RestMethod "http://127.0.0.1:$port/api/status" -TimeoutSec 2
            $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -eq $now.ProcessId -and $_.LocalAddress -eq '127.0.0.1' }
            if ($listener -and $live.live.at -ge $after -and !$live.live.storageError) { return }
        } catch { }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $until)
    throw 'Service did not write a fresh collection round.'
}
function Change-Path($value) {
    $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $value }
    if ($result.ReturnValue -ne 0) { throw "Service path update returned $($result.ReturnValue)." }
}
function Save-Monitor($path) {
    $bin = [regex]::Match($service.PathName, '^"([^"]+)"').Groups[1].Value
    if (-not $bin -or -not (Test-Path -LiteralPath $bin -PathType Leaf)) { throw 'Installed executable could not be located.' }
    $path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
    if (Test-Path -LiteralPath $path) { throw 'Backup destination already exists.' }
    if (-not (Test-Path -LiteralPath (Join-Path $store 'monitor.db'))) { throw 'No monitor database is available.' }
    Native $bin @('--root', $root, '--backup', $path)
    Copy-Item -LiteralPath (Join-Path $root 'config.json') -Destination "$path.config.json"
}
switch ($Action) {
    'Backup' {
        if (-not $service -or -not $Destination) { throw 'An installed service and -Destination are required.' }
        Save-Monitor $Destination
        return
    }
    'Disable' { if (-not $service) { throw 'Service is missing.' }; Stop-Monitor; Set-Service $name -StartupType Disabled; return }
    'Enable' { if (-not $service) { throw 'Service is missing.' }; Set-Service $name -StartupType Automatic; Start-Monitor; return }
    'Uninstall' {
        if ($service) { Stop-Monitor; Native sc.exe @('delete', $name) }
        Write-Output "Configuration and history remain in $root."
        return
    }
}
if (($Action -eq 'Install' -and $service) -or ($Action -eq 'Upgrade' -and -not $service)) { throw 'Use Install for a new service or Upgrade for an existing one.' }
$source = (Resolve-Path -LiteralPath $Package).Path
if (-not (Test-Path -LiteralPath (Join-Path $source 'HomeNetMonitor.exe') -PathType Leaf)) { throw 'Publish the self-contained package first.' }
foreach ($directory in @($root, $store, $program)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a reparse point at $directory." }
}
Native icacls.exe @($root, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F', '*S-1-5-19:(OI)(CI)RX')
Native icacls.exe @($store, '/grant:r', '*S-1-5-19:(OI)(CI)M')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$version = Join-Path $program $stamp
New-Item -ItemType Directory -Path $version | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $version -Recurse
$exe = Join-Path $version 'HomeNetMonitor.exe'
$config = Join-Path $root 'config.json'
if (-not (Test-Path -LiteralPath $config)) { Native $exe @('--write-default-config', $config) }
$command = '"' + $exe + '" --root "' + $root + '"'
if ($service) {
    $previous = $service.PathName
    $backup = Join-Path $root "before-$stamp.db"
    Stop-Monitor
    try {
        if (Test-Path -LiteralPath (Join-Path $store 'monitor.db')) { Save-Monitor $backup }
    } catch { Start-Monitor; throw }
    try {
        $after = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Change-Path $command
        Start-Monitor
        Wait-Monitor $after
    } catch {
        Stop-Monitor
        if (Test-Path -LiteralPath $backup) {
            $failed = Join-Path $root "failed-$stamp"
            New-Item -ItemType Directory -Path $failed | Out-Null
            foreach ($file in 'monitor.db','monitor.db-wal','monitor.db-shm') {
                $item = Join-Path $store $file
                if (Test-Path -LiteralPath $item) { Move-Item -LiteralPath $item -Destination $failed }
            }
            Copy-Item -LiteralPath $backup -Destination (Join-Path $store 'monitor.db')
        }
        Change-Path $previous
        Start-Monitor
        throw
    }
} else {
    New-Service -Name $name -BinaryPathName $command -DisplayName 'Home Network Monitor' -StartupType Automatic | Out-Null
    try {
        Native sc.exe @('config', $name, 'obj=', 'NT AUTHORITY\LocalService')
        Native sc.exe @('failure', $name, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000')
        Native sc.exe @('failureflag', $name, '1')
        Start-Monitor
    }
    catch { Native sc.exe @('delete', $name); throw }
}
$port = (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).port
Write-Output "HomeNetMonitor started at http://127.0.0.1:$port/"
