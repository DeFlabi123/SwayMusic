#requires -Version 7.0
param([string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'SwayMusic-ReleaseKeys\update-signing-private-key.pem'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SwayMusicDesktop'
    if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'SwayMusicDesktop'))) {
        throw 'An existing SwayMusic installation was found. Run this verification in a clean Windows user or VM.'
    }
    $testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts\installer-verification-' + [Guid]::NewGuid().ToString('N'))))
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
    if (!$testRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected verification directory.' }
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $installRoot = Join-Path $testRoot 'installed'
    $payload = Join-Path $testRoot 'payload'
    $feed = Join-Path $testRoot 'feed'
    $shortcuts = @(
        (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'SwayMusic.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'SwayMusic.lnk')
    )
    $savedShortcuts = @{}
    foreach ($shortcut in $shortcuts) { if (Test-Path -LiteralPath $shortcut) { $savedShortcuts[$shortcut] = [IO.File]::ReadAllBytes($shortcut) } }
    $installed = $false
    try {
        $setup = Join-Path $projectRoot 'artifacts\installer\SwayMusic-Setup.exe'
        $process = Start-Process -FilePath $setup -ArgumentList @('--silent','--installto', ('"{0}"' -f $installRoot)) -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(60000) -or $process.ExitCode -ne 0) { throw 'Silent installer verification failed.' }
        $installed = $true
        foreach ($shortcut in $shortcuts) { if (!(Test-Path -LiteralPath $shortcut)) { throw "Missing shortcut: $shortcut" } }
        & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Directory (Join-Path $installRoot 'current') -ReportName 'installed-release-check.json'
        $initial = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installRoot 'current\SwayMusic.exe')).ProductVersion.Split('+')[0]
        $parts = $initial.Split('.')
        $next = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
        $env:DOTNET_CLI_HOME = $projectRoot
        $dotnetCommand = if (Test-Path -LiteralPath '.sdk\dotnet.exe') { Join-Path $projectRoot '.sdk\dotnet.exe' } else { 'dotnet' }
        & $dotnetCommand publish src\SwayMusic.Desktop\SwayMusic.Desktop.csproj -c Release -r win-x64 --self-contained true --output $payload --packages .nuget\packages "-p:Version=$next" -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE) { throw 'The second version could not be published.' }
        Copy-Item -LiteralPath LICENSE,THIRD-PARTY-NOTICES.md,README.md,FEATURES.md,PROGRESS.md -Destination $payload
        Copy-Item -LiteralPath docs,licenses -Destination $payload -Recurse -Force
        & $dotnetCommand tool run vpk -- pack --packId SwayMusicDesktop --packVersion $next --packDir $payload --mainExe SwayMusic.exe --packTitle SwayMusic --runtime win-x64 --channel win-x64-stable --delta None --outputDir $feed
        if ($LASTEXITCODE) { throw 'The second version could not be packaged.' }
        $full = Get-ChildItem -LiteralPath $feed -File -Filter '*-full.nupkg'
        $metadata = [ordered]@{ schemaVersion=1; packageId='SwayMusicDesktop'; channel='win-x64-stable'; architecture='win-x64'; version=$next; fileName=$full.Name; size=$full.Length;
            sha256=(Get-FileHash -LiteralPath $full.FullName -Algorithm SHA256).Hash; sha1=(Get-FileHash -LiteralPath $full.FullName -Algorithm SHA1).Hash; notes='Installer upgrade verification fixture.' } | ConvertTo-Json -Compress
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($metadata)
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $privatePem = if ($env:UPDATE_SIGNING_PRIVATE_KEY) { $env:UPDATE_SIGNING_PRIVATE_KEY } else { [IO.File]::ReadAllText($PrivateKeyPath) }
            $key.ImportFromPem($privatePem)
            $signature = $key.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        }
        finally { $key.Dispose(); $privatePem = $null }
        [IO.File]::WriteAllBytes((Join-Path $feed 'sway-update.json'), $bytes)
        [IO.File]::WriteAllText((Join-Path $feed 'sway-update.sig'), [Convert]::ToBase64String($signature))
        & .\tests\SwayMusic.Tests\bin\Release\net10.0-windows10.0.19041.0\SwayMusic.Tests.exe --installed-update $installRoot $feed
        if ($LASTEXITCODE) { throw 'Installed A-to-B update verification failed.' }
        & (Join-Path $PSScriptRoot 'Verify-Release.ps1') -Directory (Join-Path $installRoot 'current') -ReportName 'updated-release-check.json'
        [ordered]@{ InitialVersion=$initial; UpdatedVersion=$next; Installed=$true; Shortcuts=$true; SignedUpdate=$true; UpdatedLaunch=$true } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactRoot 'installer-verification.json')
    }
    finally {
        if ($installed -and (Test-Path -LiteralPath (Join-Path $installRoot 'Update.exe'))) {
            $uninstaller = Start-Process -FilePath (Join-Path $installRoot 'Update.exe') -ArgumentList @('--silent','uninstall') -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
            if (!$uninstaller.WaitForExit(60000) -or $uninstaller.ExitCode -ne 0) { Write-Warning "Verification installation could not be uninstalled: $installRoot" }
        }
        foreach ($shortcut in $shortcuts) {
            if ($savedShortcuts.ContainsKey($shortcut)) { [IO.File]::WriteAllBytes($shortcut, $savedShortcuts[$shortcut]) }
        }
    }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'current\SwayMusic.exe')) { throw 'Uninstall left the test application installed.' }
    if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall left its registration behind.' }
    $reportPath = Join-Path $artifactRoot 'installer-verification.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $report | Add-Member -NotePropertyName Uninstalled -NotePropertyValue $true
    $report | ConvertTo-Json | Set-Content -LiteralPath $reportPath
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (!$resolvedTestRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe verification cleanup path.' }
    # Velopack's self-removal helper can briefly retain the working directory after uninstall exits.
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force; break }
        catch [IO.IOException] { if ($attempt -eq 39) { throw }; Start-Sleep -Milliseconds 250 }
    }
    Write-Output 'PASS installer, shortcuts, installed startup, signed A-to-B update, updated startup and uninstall.'
}
finally { Pop-Location }
