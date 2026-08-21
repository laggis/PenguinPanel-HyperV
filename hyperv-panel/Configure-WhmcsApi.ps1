param(
    [string[]]$AllowedIp = @(),
    [switch]$TrustForwardedFor,
    [switch]$AllowHttp
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot 'appsettings.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Could not find appsettings.json next to this script: $configPath"
}

$random = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($random) } finally { $rng.Dispose() }
$key = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+','-').Replace('/','_')

$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key))
} finally {
    $sha.Dispose()
}
$hash = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })

$json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$api = [pscustomobject]@{
    Enabled = $true
    RequireHttps = -not [bool]$AllowHttp
    ApiKeySha256 = $hash
    AllowedIps = @($AllowedIp | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
    TrustForwardedFor = [bool]$TrustForwardedFor
    MaxRequestsPerMinute = 120
}

if ($null -eq $json.WhmcsApi) {
    $json | Add-Member -NotePropertyName WhmcsApi -NotePropertyValue $api
} else {
    $json.WhmcsApi = $api
}

$json | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding UTF8

Write-Host ''
Write-Host 'PenguinPanel WHMCS API is now enabled.' -ForegroundColor Green
Write-Host 'SAVE THIS API KEY NOW. It is only stored as a SHA-256 hash in appsettings.json.' -ForegroundColor Yellow
Write-Host ''
Write-Host "API key: $key" -ForegroundColor Cyan
Write-Host "SHA256 : $hash"
Write-Host ''
Write-Host 'Restart PenguinPanel after changing appsettings.json.' -ForegroundColor Yellow
if ($api.AllowedIps.Count -eq 0) {
    Write-Host 'AllowedIps is empty, so any source IP can authenticate with the API key. Add the WHMCS server IP later for another security layer.' -ForegroundColor Yellow
} else {
    Write-Host ('Allowed IPs: ' + ($api.AllowedIps -join ', '))
}
