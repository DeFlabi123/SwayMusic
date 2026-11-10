param([string]$Destination = (Join-Path $PSScriptRoot '..\tools'))
$ErrorActionPreference = 'Stop'
$targetDirectory = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
$assets = @(
    @{ Name='yt-dlp.exe'; Url='https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe'; Hash='f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d' },
    @{ Name='qjs.exe'; Url='https://github.com/quickjs-ng/quickjs/releases/download/v0.18.0/qjs-windows-x86_64.exe'; Hash='ff03398d74ea6e6bd3a8e58058f7003225abcac229d7d57775b7aaae812a49c7' }
)
foreach ($asset in $assets) {
    $assetPath = Join-Path $targetDirectory $asset.Name
    if (!(Test-Path -LiteralPath $assetPath)) {
        Invoke-WebRequest -Uri $asset.Url -OutFile $assetPath -TimeoutSec 120
    }
    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
    if ($actualHash -ne $asset.Hash) { throw "SHA256 mismatch for $($asset.Name). Do not run this file." }
    Write-Output "Verified $($asset.Name) SHA256 $actualHash"
}
$extractorLicense = Join-Path $targetDirectory 'yt-dlp-THIRD_PARTY_LICENSES.txt'
$quickJsLicense = Join-Path $targetDirectory 'quickjs-LICENSE.txt'
if (!(Test-Path -LiteralPath $extractorLicense)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.10.26/THIRD_PARTY_LICENSES.txt' -OutFile $extractorLicense -TimeoutSec 30
}
if (!(Test-Path -LiteralPath $quickJsLicense)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/quickjs-ng/quickjs/v0.18.0/LICENSE' -OutFile $quickJsLicense -TimeoutSec 30
}
