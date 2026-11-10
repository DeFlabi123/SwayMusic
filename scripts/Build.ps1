param([switch]$Test, [switch]$Live, [switch]$Audio, [switch]$Headless, [switch]$Desktop)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = $projectRoot
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $dotnetCommand = if (Test-Path -LiteralPath '.sdk\dotnet.exe') { Join-Path $projectRoot '.sdk\dotnet.exe' } else { 'dotnet' }
    if ($dotnetCommand -ne 'dotnet') {
        $env:DOTNET_ROOT = Join-Path $projectRoot '.sdk'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    }
    & $dotnetCommand restore Metrolist.slnx --packages .nuget\packages
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    & $dotnetCommand build Metrolist.slnx -c Release --no-restore
    if ($LASTEXITCODE) { throw 'Build failed.' }
    if ($Test -or $Live -or $Audio) {
        $testArgs = @()
        if ($Audio) { $testArgs += '--audio' }
        if ($Live) { $testArgs += '--live' }
        & '.\tests\Metrolist.Tests\bin\Release\net10.0-windows10.0.19041.0\Metrolist.Tests.exe' @testArgs
        if ($LASTEXITCODE) { throw 'Verification failed.' }
    }
    if ($Headless) {
        & '.\tests\Metrolist.Tests\bin\Release\net10.0-windows10.0.19041.0\Metrolist.Tests.exe' --headless
        if ($LASTEXITCODE) { throw 'Headless UI verification failed.' }
    }
    if ($Desktop) {
        & '.\tests\Metrolist.Tests\bin\Release\net10.0-windows10.0.19041.0\Metrolist.Tests.exe' --desktop-smoke
        if ($LASTEXITCODE) { throw 'Native window verification failed.' }
    }
}
finally { Pop-Location }
