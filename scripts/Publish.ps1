param([switch]$FrameworkDependent, [switch]$SkipTools)
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
    $folderName = if ($FrameworkDependent) { 'SwayMusic-win-x64-framework-dependent' } else { 'SwayMusic-win-x64' }
    $publishDirectory = Join-Path $projectRoot "artifacts\$folderName"
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & $dotnetCommand publish src\SwayMusic.Desktop\SwayMusic.Desktop.csproj -c Release -r win-x64 --self-contained $selfContained --output $publishDirectory --packages .nuget\packages -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    Copy-Item -LiteralPath LICENSE,THIRD-PARTY-NOTICES.md,README.md,FEATURES.md,PROGRESS.md -Destination $publishDirectory
    Copy-Item -LiteralPath docs -Destination $publishDirectory -Recurse -Force
    Copy-Item -LiteralPath licenses -Destination $publishDirectory -Recurse -Force
    if (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Filter 'Metrolist*.dll') { throw 'Old Metrolist assemblies were found in the SwayMusic distribution.' }
    if (Test-Path -LiteralPath (Join-Path $publishDirectory 'Metrolist.exe')) { throw 'An old Metrolist executable was found in the SwayMusic distribution.' }
    $zipPath = Join-Path $projectRoot "artifacts\$folderName.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force
    $files = Get-ChildItem -LiteralPath $publishDirectory -File -Recurse
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Output "Published $($files.Count) files; $([Math]::Round($bytes / 1MB, 2)) MiB."
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
}
finally { Pop-Location }
