<#
.SYNOPSIS
    Generates a self-signed certificate for local development of the MiddleTierApi.

.DESCRIPTION
    Creates a .pfx file (private + public key, loaded by the code at runtime) and a
    .cer file (public key only, upload to Azure Portal → App registrations →
    MiddleTierApi → Certificates & secrets → Certificates).

.PARAMETER Subject
    Certificate subject name. Default: "CN=LearnOBO-MiddleTierApi"

.PARAMETER DaysValid
    How many days the certificate is valid. Default: 30

.PARAMETER PfxPassword
    Password protecting the .pfx file. Default: "dev-only-password"

.PARAMETER OutputDir
    Directory where .pfx and .cer files are written. Default: MiddleTierApi/

.EXAMPLE
    .\New-DevCertificate.ps1
    .\New-DevCertificate.ps1 -DaysValid 7
    .\New-DevCertificate.ps1 -Subject "CN=MyApp" -PfxPassword "s3cret" -OutputDir ".\certs"
#>
param(
    [string]$Subject = "CN=LearnOBO-MiddleTierApi",
    [int]$DaysValid = 30,
    [string]$PfxPassword = "dev-only-password",
    [string]$OutputDir = (Join-Path $PSScriptRoot "MiddleTierApi")
)

$ErrorActionPreference = "Stop"

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$pfxPath = Join-Path $OutputDir "MiddleTierApi.pfx"
$cerPath = Join-Path $OutputDir "MiddleTierApi.cer"

# Create a self-signed certificate in a temporary cert store location
$cert = New-SelfSignedCertificate `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -KeyLength 2048 `
    -NotAfter (Get-Date).AddDays($DaysValid)

try {
    # Export .pfx (private + public key) — used by the code at runtime
    $securePassword = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null

    # Export .cer (public key only) — upload to Azure Portal
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
}
finally {
    # Remove from the Windows certificate store — we only need the files
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
}

Write-Host ""
Write-Host "Certificate generated successfully!" -ForegroundColor Green
Write-Host "  Subject:    $Subject"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Valid:      $(Get-Date -Format 'yyyy-MM-dd') to $((Get-Date).AddDays($DaysValid).ToString('yyyy-MM-dd')) ($DaysValid days)"
Write-Host ""
Write-Host "Files:"
Write-Host "  $pfxPath  (loaded by code — keep secret)"
Write-Host "  $cerPath  (upload to Azure Portal → App registrations → Certificates & secrets)"
Write-Host ""
