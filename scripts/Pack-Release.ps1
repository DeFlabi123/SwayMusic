#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$ReleaseNotes = (Join-Path $PSScriptRoot '..\RELEASE_NOTES.md'),
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'SwayMusic-ReleaseKeys\update-signing-private-key.pem'),
    [switch]$RequireWindowsSigning
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Update-Signing.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = $projectRoot
    $dotnetCommand = if (Test-Path -LiteralPath '.sdk\dotnet.exe') { Join-Path $projectRoot '.sdk\dotnet.exe' } else { 'dotnet' }
    $privatePem = if ($env:UPDATE_SIGNING_PRIVATE_KEY) { $env:UPDATE_SIGNING_PRIVATE_KEY }
        elseif (Test-Path -LiteralPath $PrivateKeyPath) { [IO.File]::ReadAllText($PrivateKeyPath) }
        else { throw 'Set the UPDATE_SIGNING_PRIVATE_KEY GitHub Actions secret, or run New-UpdateSigningKey.ps1 locally.' }
    $publicPem = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\SwayMusic.Desktop\Updates\update-public-key.pem'))
    $key = Import-SwayUpdateSigningKey -PrivatePem $privatePem -PublicPem $publicPem
    try {
        if ($RequireWindowsSigning -and !$env:VPK_SIGN_TEMPLATE -and !$env:VPK_AZURE_TRUSTED_SIGN_FILE) {
            throw 'Windows signing is required: configure VPK_SIGN_TEMPLATE or VPK_AZURE_TRUSTED_SIGN_FILE.'
        }
        if (!$env:VPK_SIGN_TEMPLATE -and !$env:VPK_AZURE_TRUSTED_SIGN_FILE) {
            Write-Warning 'This test installer has no Windows Authenticode signature. Update metadata will still be publisher-signed.'
        }
        & (Join-Path $PSScriptRoot 'Publish.ps1') -Version $Version
        $payload = Join-Path $projectRoot 'artifacts\SwayMusic-win-x64'
        $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'SwayMusic.exe')).ProductVersion.Split('+')[0]
        if ($actualVersion -ne $Version) { throw "Published version $actualVersion does not match requested version $Version." }
        $output = Join-Path $projectRoot 'artifacts\installer'
        $expectedOutput = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\installer'))
        if ([IO.Path]::GetFullPath($output) -ne $expectedOutput) { throw 'Unexpected package output directory.' }
        if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
        New-Item -ItemType Directory -Path $output | Out-Null
        & $dotnetCommand tool restore
        if ($LASTEXITCODE) { throw 'Packaging tool restore failed.' }
        & $dotnetCommand tool run vpk -- pack --packId SwayMusicDesktop --packVersion $Version --packDir $payload `
            --mainExe SwayMusic.exe --packTitle SwayMusic --packAuthors 'SwayMusic contributors' `
            --runtime win-x64 --channel win-x64-stable --delta None --releaseNotes $ReleaseNotes `
            --icon src\SwayMusic.Desktop\Assets\sway.ico --outputDir $output
        if ($LASTEXITCODE) { throw 'Installer packaging failed.' }
        $full = @(Get-ChildItem -LiteralPath $output -File -Filter '*-full.nupkg')
        $setup = @(Get-ChildItem -LiteralPath $output -File -Filter '*Setup.exe')
        if ($full.Count -ne 1 -or $setup.Count -ne 1) { throw 'Expected exactly one full package and setup executable.' }
        Move-Item -LiteralPath $setup[0].FullName -Destination (Join-Path $output 'SwayMusic-Setup.exe')
        # Portable archives are not part of the installer release.
        Get-ChildItem -LiteralPath $output -File -Filter '*Portable.zip' | Remove-Item -Force
        Remove-Item -LiteralPath (Join-Path $output 'assets.win-x64-stable.json') -ErrorAction SilentlyContinue
        $notes = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ReleaseNotes))
        if ($notes.Length -gt 32768) { throw 'Release notes exceed the updater limit.' }
        $metadata = [ordered]@{
            schemaVersion = 1; packageId = 'SwayMusicDesktop'; channel = 'win-x64-stable'; architecture = 'win-x64'
            version = $Version; fileName = $full[0].Name; size = $full[0].Length
            sha256 = (Get-FileHash -LiteralPath $full[0].FullName -Algorithm SHA256).Hash
            sha1 = (Get-FileHash -LiteralPath $full[0].FullName -Algorithm SHA1).Hash; notes = $notes
        } | ConvertTo-Json -Compress
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($metadata)
        $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        [IO.File]::WriteAllBytes((Join-Path $output 'sway-update.json'), $bytes)
        [IO.File]::WriteAllText((Join-Path $output 'sway-update.sig'), [Convert]::ToBase64String($signature), [Text.UTF8Encoding]::new($false))
        & (Join-Path $PSScriptRoot 'Source-Archive.ps1')
        Copy-Item -LiteralPath artifacts\SwayMusic-Windows-source.zip -Destination $output
        Remove-Item -LiteralPath artifacts\SwayMusic-Windows-source.zip
        $checksums = Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {
            '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
        }
        [IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
        Write-Output "Installer and authenticated update assets: $output"
    }
    finally { $key.Dispose(); $privatePem = $null }
}
finally { Pop-Location }
