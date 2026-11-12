#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Update-Signing.ps1')

$fixture = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
$different = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $privateLf = $fixture.ExportPkcs8PrivateKeyPem() -replace '\r?\n', "`n"
    $publicLf = $fixture.ExportSubjectPublicKeyInfoPem() -replace '\r?\n', "`n"
    $privateCrLf = $privateLf -replace "`n", "`r`n"
    $publicCrLf = $publicLf -replace "`n", "`r`n"
    foreach ($privateText in @($privateLf, $privateCrLf)) {
        foreach ($publicText in @($publicLf, $publicCrLf, "`r`n$publicCrLf`r`n")) {
            $imported = Import-SwayUpdateSigningKey -PrivatePem $privateText -PublicPem $publicText
            try {
                $message = [Text.Encoding]::UTF8.GetBytes('release-signing-regression')
                $signature = $imported.SignData($message, [Security.Cryptography.HashAlgorithmName]::SHA256)
                if (!$fixture.VerifyData($message, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256)) {
                    throw 'Imported key did not produce a valid signature.'
                }
            }
            finally { $imported.Dispose() }
        }
    }
    foreach ($invalid in @($different.ExportPkcs8PrivateKeyPem(), $publicLf, 'C:\keys\private-key.pem', 'invalid PEM text')) {
        $rejected = $false
        try {
            $unexpected = Import-SwayUpdateSigningKey -PrivatePem $invalid -PublicPem $publicCrLf
            $unexpected.Dispose()
        }
        catch { $rejected = $true }
        if (!$rejected) { throw 'A mismatched or invalid private key was accepted.' }
    }
    Write-Output 'PASS release signing: LF/CRLF combinations, surrounding whitespace, actual signature verification, wrong-key rejection, public-only rejection and invalid PEM/path rejection.'
}
finally { $fixture.Dispose(); $different.Dispose() }
