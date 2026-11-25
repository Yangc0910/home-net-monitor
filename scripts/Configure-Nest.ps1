#Requires -Version 7.0
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][guid]$DeviceAccessProject,
    [Parameter(Mandatory)][string]$Subscription,
    [Parameter(Mandatory)][string]$OAuthClientFile,
    [Parameter(Mandatory)][string]$SubscriberKeyFile,
    [switch]$UseDialog
)
$ErrorActionPreference = 'Stop'
if ($Subscription -notmatch '^projects/[A-Za-z0-9_-]+/subscriptions/[A-Za-z0-9._~-]+$') { throw 'Expected projects/PROJECT/subscriptions/SUBSCRIPTION.' }
$root = Join-Path $env:ProgramData 'HomeNetMonitor'
$configPath = Join-Path $root 'config.json'
if (!(Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'Install the Windows service first.' }
$client = (Get-Content -LiteralPath $OAuthClientFile -Raw | ConvertFrom-Json).web
$redirect = 'https://www.google.com'
if (!$client.client_id -or !$client.client_secret -or $redirect -notin $client.redirect_uris) { throw 'Use a Web OAuth client with https://www.google.com as an authorized redirect URI.' }
$keyText = Get-Content -LiteralPath $SubscriberKeyFile -Raw
$key = $keyText | ConvertFrom-Json
if ($key.type -ne 'service_account' -or $key.token_uri -ne 'https://oauth2.googleapis.com/token' -or !$key.private_key) { throw 'Use a Google Pub/Sub subscriber service-account key file.' }

$project = $DeviceAccessProject.ToString()
$params = @{
    redirect_uri=$redirect; access_type='offline'; prompt='consent'; client_id=$client.client_id
    response_type='code'; scope='https://www.googleapis.com/auth/sdm.service'
}
$query = ($params.GetEnumerator() | ForEach-Object { $_.Key + '=' + [uri]::EscapeDataString($_.Value) }) -join '&'
Write-Output "Open this URL in your browser and authorize only the thermostat you intend to monitor."
Write-Output "https://nestservices.google.com/partnerconnections/$project/auth?$query"
if ($UseDialog) {
    Add-Type -AssemblyName System.Windows.Forms
    $form = [Windows.Forms.Form]::new()
    $form.Text = 'HomeNetMonitor - Google Nest authorization'
    $form.Width = 690; $form.Height = 235; $form.StartPosition = 'CenterScreen'; $form.TopMost = $true
    $label = [Windows.Forms.Label]::new()
    $label.Text = 'Complete Google authorization, then paste the full Google redirect URL below.'
    $label.SetBounds(20,20,640,40)
    $inputBox = [Windows.Forms.TextBox]::new()
    $inputBox.UseSystemPasswordChar = $true; $inputBox.SetBounds(20,70,630,30)
    $submit = [Windows.Forms.Button]::new()
    $submit.Text = 'Save and connect'; $submit.SetBounds(440,120,190,36)
    $submit.DialogResult = [Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $submit
    $form.Controls.AddRange(@($label,$inputBox,$submit))
    if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { $form.Dispose(); throw 'Authorization dialog canceled.' }
    $code = $inputBox.Text.Trim()
    $inputBox.Clear(); $form.Dispose()
} else {
    $secure = Read-Host 'Paste the authorization code or the full Google redirect URL here' -AsSecureString
    $code = [pscredential]::new('Authorization code', $secure).GetNetworkCredential().Password.Trim()
}
if ($code -match '^https?://') {
    $authorizationUri = [uri]$code
    if ($authorizationUri.Scheme -ne 'https' -or $authorizationUri.Host -ne 'www.google.com' -or !$authorizationUri.IsDefaultPort -or $authorizationUri.UserInfo -or $authorizationUri.AbsolutePath -ne '/') {
        throw 'Paste the redirect URL from https://www.google.com or the code parameter alone.'
    }
    $code = [System.Web.HttpUtility]::ParseQueryString($authorizationUri.Query)['code']
}
if ([string]::IsNullOrWhiteSpace($code)) { throw 'The authorization code is missing.' }
try {
    $token = Invoke-RestMethod 'https://oauth2.googleapis.com/token' -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id=$client.client_id; client_secret=$client.client_secret; code=$code
        grant_type='authorization_code'; redirect_uri=$redirect
    }
} catch { throw 'Google did not accept the authorization code. Check the client and request a fresh code.' }
if (!$token.refresh_token) { throw 'Google returned no refresh token. Authorize again with offline access.' }
try {
    $list = Invoke-RestMethod "https://smartdevicemanagement.googleapis.com/v1/enterprises/$project/devices" -Headers @{Authorization="Bearer $($token.access_token)"}
} catch { throw 'Could not read the authorized devices. Check the Device Access project and Google account.' }
$devices = @($list.devices | Where-Object { $_.type -eq 'sdm.devices.types.THERMOSTAT' -and $_.traits.'sdm.devices.traits.ThermostatHvac' })
if ($devices.Count -eq 0) { throw 'No authorized thermostat reported the HVAC state trait.' }
$index = 0
if ($devices.Count -gt 1) {
    for ($i=0; $i -lt $devices.Count; $i++) {
        Write-Output ("{0}. {1} {2}" -f ($i+1),$devices[$i].traits.'sdm.devices.traits.Info'.customName,$devices[$i].name.Split('/')[-1])
    }
    $choice = 0
    if ($UseDialog) {
        $picker = [Windows.Forms.Form]::new()
        $picker.Text = 'HomeNetMonitor - Select thermostat'; $picker.Width = 530; $picker.Height = 190; $picker.StartPosition = 'CenterScreen'; $picker.TopMost = $true
        $listBox = [Windows.Forms.ComboBox]::new(); $listBox.DropDownStyle = 'DropDownList'; $listBox.SetBounds(20,20,470,30)
        foreach ($device in $devices) {
            $title = $device.traits.'sdm.devices.traits.Info'.customName
            if (!$title) { $title = $device.parentRelations.displayName -join ' / ' }
            if (!$title) { $title = $device.name.Split('/')[-1] }
            [void]$listBox.Items.Add($title)
        }
        $listBox.SelectedIndex = 0
        $ok = [Windows.Forms.Button]::new(); $ok.Text = 'Connect selected thermostat'; $ok.SetBounds(250,75,240,34); $ok.DialogResult = 'OK'
        $picker.Controls.AddRange(@($listBox,$ok)); $picker.AcceptButton = $ok
        if ($picker.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { $picker.Dispose(); throw 'Thermostat selection canceled.' }
        $choice = $listBox.SelectedIndex + 1; $picker.Dispose()
    } else {
        if (![int]::TryParse((Read-Host 'Select a thermostat by number'), [ref]$choice) -or $choice -lt 1 -or $choice -gt $devices.Count) { throw 'Invalid thermostat selection.' }
    }
    $index = $choice - 1
}
$credentialsName = 'nest.credentials.json'
$keyName = 'nest.pubsub-key.json'
$deviceId = $devices[$index].name.Split('/')[-1]
$credentials = @{
    deviceAccessProject=$project; deviceId=$deviceId; subscription=$Subscription
    clientId=$client.client_id; clientSecret=$client.client_secret; refreshToken=$token.refresh_token
    subscriberKeyFile=$keyName
}
function Write-Config($file, $value) {
    $dest = Join-Path $root $file
    if ((Test-Path -LiteralPath $dest) -and ((Get-Item -LiteralPath $dest).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unexpected reparse point at $file." }
    $temp = Join-Path $root ([guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temp, $value)
    [IO.File]::Move($temp, $dest, $true)
}
Write-Config $keyName $keyText
Write-Config $credentialsName ($credentials | ConvertTo-Json -Depth 5)
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config | Add-Member -Force -NotePropertyName 'nest' -NotePropertyValue @{enabled=$true;credentialsFile=$credentialsName}
Write-Config 'config.json' ($config | ConvertTo-Json -Depth 16)
Write-Output 'Nest authorization saved to the service directory. Restart HomeNetMonitor to connect the subscription.'
