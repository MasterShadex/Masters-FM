param([string]$MsiPath = '')

$ErrorActionPreference = 'Stop'

$certSubject = 'CN=MasterShadex, O=MasterShadex'
$friendly    = "Master's FM Code Signing"

$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $certSubject -and $_.FriendlyName -eq $friendly } |
        Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "No existing cert found - creating self-signed..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubject `
        -FriendlyName $friendly `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host ("Created cert thumbprint: " + $cert.Thumbprint)

    $outCer = Join-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) 'MastersFM_publisher.cer'
    Export-Certificate -Cert $cert -FilePath $outCer -Type CERT | Out-Null
    Write-Host ("Exported public cert to: " + $outCer)
}

if (-not $MsiPath -or -not (Test-Path $MsiPath)) {
    Write-Host "MsiPath not provided or missing - skipping signature."
    exit 0
}

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -match 'x64' } |
            Sort-Object FullName -Descending | Select-Object -First 1

if ($signtool) {
    Write-Host ("Using signtool at " + $signtool.FullName)
    & $signtool.FullName sign /sha1 $cert.Thumbprint /fd SHA256 /t 'http://timestamp.digicert.com' $MsiPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("WARN: signtool exit=" + $LASTEXITCODE + " - falling back to Set-AuthenticodeSignature")
        Set-AuthenticodeSignature -FilePath $MsiPath -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256 | Out-Null
    }
} else {
    Write-Host "signtool not found, using Set-AuthenticodeSignature"
    Set-AuthenticodeSignature -FilePath $MsiPath -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256 | Out-Null
}

$sig = Get-AuthenticodeSignature $MsiPath
Write-Host ("Signature status: " + $sig.Status)
if ($sig.SignerCertificate) { Write-Host ("Signer: " + $sig.SignerCertificate.Subject) }
