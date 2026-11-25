param(
    [string[]]$AllowedIp,
    [switch]$TrustForwardedFor,
    [switch]$AllowHttp,
    [switch]$RotateKey
)

$ErrorActionPreference = 'Stop'
$configPath = Join-Path $PSScriptRoot 'appsettings.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Could not find appsettings.json next to this script: $configPath"
}

$json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$existingApi = $json.WhmcsApi
$existingHash = if ($null -ne $existingApi) { [string]$existingApi.ApiKeySha256 } else { '' }
$existingHash = $existingHash.Trim()
$hasUsableExistingHash = $existingHash -match '^[0-9a-fA-F]{64}$'

$key = $null
$hash = $existingHash
$generatedNewKey = [bool]$RotateKey -or -not $hasUsableExistingHash

if ($generatedNewKey) {
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
}

$requireHttps = if ($PSBoundParameters.ContainsKey('AllowHttp')) {
    -not [bool]$AllowHttp
} elseif ($null -ne $existingApi -and $null -ne $existingApi.RequireHttps) {
    [bool]$existingApi.RequireHttps
} else {
    $true
}

$trustForwardedForValue = if ($PSBoundParameters.ContainsKey('TrustForwardedFor')) {
    [bool]$TrustForwardedFor
} elseif ($null -ne $existingApi -and $null -ne $existingApi.TrustForwardedFor) {
    [bool]$existingApi.TrustForwardedFor
} else {
    $false
}

$allowedIps = if ($PSBoundParameters.ContainsKey('AllowedIp')) {
    @($AllowedIp | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
} elseif ($null -ne $existingApi -and $null -ne $existingApi.AllowedIps) {
    @($existingApi.AllowedIps | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ([string]$_).Trim() })
} else {
    @()
}

$maxRequestsPerMinute = 120
if ($null -ne $existingApi -and $null -ne $existingApi.MaxRequestsPerMinute) {
    [int]$parsedLimit = 0
    if ([int]::TryParse([string]$existingApi.MaxRequestsPerMinute, [ref]$parsedLimit) -and $parsedLimit -ge 10 -and $parsedLimit -le 5000) {
        $maxRequestsPerMinute = $parsedLimit
    }
}

$api = [pscustomobject]@{
    Enabled = $true
    RequireHttps = $requireHttps
    ApiKeySha256 = $hash
    AllowedIps = $allowedIps
    TrustForwardedFor = $trustForwardedForValue
    MaxRequestsPerMinute = $maxRequestsPerMinute
}

if ($null -eq $json.WhmcsApi) {
    $json | Add-Member -NotePropertyName WhmcsApi -NotePropertyValue $api
} else {
    $json.WhmcsApi = $api
}

$json | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding UTF8

Write-Host ''
Write-Host 'PenguinPanel WHMCS API is now enabled.' -ForegroundColor Green
if ($generatedNewKey) {
    Write-Host 'SAVE THIS API KEY NOW. It is only stored as a SHA-256 hash in appsettings.json.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "API key: $key" -ForegroundColor Cyan
    Write-Host "SHA256 : $hash"
    if ($RotateKey) {
        Write-Host 'The key was deliberately rotated. Replace the PenguinPanel server Password in WHMCS with the new API key.' -ForegroundColor Yellow
    } else {
        Write-Host 'No existing configured key was found. Put this API key in the PenguinPanel server Password field in WHMCS.' -ForegroundColor Yellow
    }
} else {
    Write-Host 'Existing API key preserved. The PenguinPanel server Password in WHMCS does not need to change.' -ForegroundColor Cyan
    Write-Host 'The plaintext key cannot be displayed again because PenguinPanel stores only its SHA-256 hash.'
    Write-Host 'To deliberately replace it later, run: .\Configure-WhmcsApi.ps1 -RotateKey' -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Restart PenguinPanel after changing appsettings.json.' -ForegroundColor Yellow
if ($api.AllowedIps.Count -eq 0) {
    Write-Host 'AllowedIps is empty, so any source IP can authenticate with the API key. Add the WHMCS server IP later for another security layer.' -ForegroundColor Yellow
} else {
    Write-Host ('Allowed IPs: ' + ($api.AllowedIps -join ', '))
}
