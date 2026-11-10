param([string]$Channel = '10.0')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installPath = Join-Path $projectRoot '.sdk'
$cachePath = Join-Path $projectRoot '.sdk-cache'
$metadata = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$Channel/releases.json"
$version = $metadata.'latest-sdk'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "The official channel did not return a stable SDK: $version" }
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $version } | Select-Object -First 1
if ($null -eq $sdk) { $sdk = $metadata.releases | ForEach-Object { $_.sdk } | Where-Object { $_.version -eq $version } | Select-Object -First 1 }
$package = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
if ($null -eq $package) { throw 'No Windows x64 SDK archive was found in the official release metadata.' }
$archiveUri = [Uri]$package.url
if ($archiveUri.Scheme -ne 'https' -or $archiveUri.Host -ne 'builds.dotnet.microsoft.com') { throw 'Unexpected SDK archive host.' }
New-Item -ItemType Directory -Force -Path $cachePath, $installPath | Out-Null
$archivePath = Join-Path $cachePath "dotnet-sdk-$version-win-x64.zip"
Write-Output "Stable .NET SDK selected from official release metadata: $version"
if (!(Test-Path -LiteralPath $archivePath))
{
    Write-Output "Downloading $($package.url)"
    Invoke-WebRequest -Uri $package.url -OutFile $archivePath
}
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash
if ($hash -ine $package.hash) { throw "SHA512 verification failed for $archivePath" }
Write-Output 'Official SHA512 verified. Extracting the portable SDK.'
Expand-Archive -LiteralPath $archivePath -DestinationPath $installPath -Force
$env:DOTNET_CLI_HOME = $projectRoot
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
& (Join-Path $installPath 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw "Portable SDK exited with $LASTEXITCODE" }
Write-Output "Portable SDK ready: $(Join-Path $installPath 'dotnet.exe')"
