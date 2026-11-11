param([switch]$SkipTools, [switch]$Archive)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = $projectRoot
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $dotnetCommand = if (Test-Path -LiteralPath '.sdk\dotnet.exe') { Join-Path $projectRoot '.sdk\dotnet.exe' } else { 'dotnet' }
    if ($dotnetCommand -ne 'dotnet') {
        $env:DOTNET_ROOT = Join-Path $projectRoot '.sdk'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    }
    if (!$SkipTools) { & (Join-Path $PSScriptRoot 'Get-StreamTools.ps1') }
    $folderName = 'SwayMusic-win-x64'
    $publishDirectory = Join-Path $projectRoot "artifacts\$folderName"
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
    foreach ($running in @(Get-Process -Name SwayMusic -ErrorAction SilentlyContinue)) {
        if ($running.Path -and $running.Path.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            [void]$running.CloseMainWindow()
            if (!$running.WaitForExit(15000)) { throw 'SwayMusic did not close cleanly. Close its window before publishing again.' }
        }
    }
    & $dotnetCommand publish src\SwayMusic.Desktop\SwayMusic.Desktop.csproj -c Release -r win-x64 --self-contained true --output $publishDirectory --packages .nuget\packages -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE,THIRD-PARTY-NOTICES.md,README.md,FEATURES.md,PROGRESS.md -Destination $publishDirectory
    Copy-Item -LiteralPath docs -Destination $publishDirectory -Recurse -Force
    Copy-Item -LiteralPath licenses -Destination $publishDirectory -Recurse -Force
    if (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Filter 'Metrolist*.dll') { throw 'Old Metrolist assemblies were found in the SwayMusic distribution.' }
    if (Test-Path -LiteralPath (Join-Path $publishDirectory 'Metrolist.exe')) { throw 'An old Metrolist executable was found in the SwayMusic distribution.' }
    if ($Archive) {
        $zipPath = Join-Path $projectRoot "artifacts\$folderName.zip"
        Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
        Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
    }
    $files = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Output "Published $($files.Count) files; $([Math]::Round($bytes / 1MB, 2)) MiB."
}
finally { Pop-Location }
