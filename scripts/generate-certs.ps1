# generate-certs.ps1
# Genereaza root CA + certificate server/client si le copiaza in bin\ pentru Client si Server.

$ErrorActionPreference = "Stop"

$scriptRoot   = $PSScriptRoot
$solutionRoot = Split-Path $scriptRoot -Parent
$outDir       = Join-Path $solutionRoot "certs"

if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " NFFI Certificate Generator" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " Output dir: $outDir"
Write-Host ""

# ---- 1. Root CA ----
Write-Host "[1/3] Generez root CA..." -ForegroundColor Cyan
$rootCA = New-SelfSignedCertificate `
    -Subject "CN=NffiTracking Root CA" `
    -KeyUsage CertSign, CRLSign, DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 4096 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(10)
Export-Certificate -Cert $rootCA -FilePath (Join-Path $outDir "rootCA.cer") | Out-Null
Write-Host "      OK: rootCA.cer" -ForegroundColor Green

# ---- 2. Server certificate ----
Write-Host "[2/3] Generez certificat server..." -ForegroundColor Cyan
$serverCert = New-SelfSignedCertificate `
    -Subject "CN=NffiTrackingServer" `
    -DnsName "localhost", "nffi.server" `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -Signer $rootCA `
    -NotAfter (Get-Date).AddYears(5)
$sp = ConvertTo-SecureString -String "server123" -Force -AsPlainText
Export-PfxCertificate -Cert $serverCert `
    -FilePath (Join-Path $outDir "server.pfx") -Password $sp | Out-Null
Write-Host "      OK: server.pfx (password: server123)" -ForegroundColor Green

# ---- 3. Client certificate ----
Write-Host "[3/3] Generez certificat client..." -ForegroundColor Cyan
$clientCert = New-SelfSignedCertificate `
    -Subject "CN=NffiTrackingClient" `
    -KeyAlgorithm RSA -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -Signer $rootCA `
    -NotAfter (Get-Date).AddYears(5)
$cp = ConvertTo-SecureString -String "client123" -Force -AsPlainText
Export-PfxCertificate -Cert $clientCert `
    -FilePath (Join-Path $outDir "client.pfx") -Password $cp | Out-Null
Write-Host "      OK: client.pfx (password: client123)" -ForegroundColor Green

# ---- 4. Copy in bin\ pentru Client si Server ----
Write-Host ""
Write-Host "Copiez certificatele in bin\ pentru Client si Server..." -ForegroundColor Cyan

$targets = @(
    (Join-Path $solutionRoot "NffiTrackingSystem.Client\bin\Debug\net8.0-windows\certs"),
    (Join-Path $solutionRoot "NffiTrackingSystem.Server\bin\Debug\net8.0-windows\certs")
)

foreach ($target in $targets) {
    if (-not (Test-Path $target)) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
    }
    Copy-Item -Path (Join-Path $outDir "*") -Destination $target -Force
    Write-Host "      OK: $target" -ForegroundColor Green
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " Certificate generate." -ForegroundColor Green
Write-Host ""
Write-Host " Locație:       $outDir"
Write-Host " Copiate si in: Client\bin\Debug\net8.0-windows\certs"
Write-Host "                Server\bin\Debug\net8.0-windows\certs"
Write-Host ""
Write-Host " Passwords:" -ForegroundColor Yellow
Write-Host "   server.pfx  -> server123"
Write-Host "   client.pfx  -> client123"
Write-Host "   rootCA.cer  -> no password"
Write-Host "=====================================================" -ForegroundColor Cyan