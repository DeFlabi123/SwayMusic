#requires -Version 7.0
param([string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'SwayMusic-ReleaseKeys'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$keyRoot = [IO.Path]::GetFullPath($KeyDirectory)
if ($keyRoot.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $keyRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Store the private release key outside the source repository.'
}
$privatePath = Join-Path $keyRoot 'update-signing-private-key.pem'
$publicPath = Join-Path $projectRoot 'src\SwayMusic.Desktop\Updates\update-public-key.pem'
if ((Test-Path -LiteralPath $privatePath) -or ![string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($publicPath))) {
    throw 'A signing key already exists. Keep it for future releases; key rotation requires an application update.'
}
New-Item -ItemType Directory -Path $keyRoot -Force | Out-Null
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    [IO.File]::WriteAllText($privatePath, $key.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($publicPath, $key.ExportSubjectPublicKeyInfoPem(), [Text.UTF8Encoding]::new($false))
}
finally { if ($key) { $key.Dispose() } }
Write-Output "Private key saved outside the repository: $privatePath"
Write-Output "Public verification key: $publicPath"
Write-Output 'Add the private PEM contents to the GitHub Actions secret UPDATE_SIGNING_PRIVATE_KEY. Back up this key securely.'
