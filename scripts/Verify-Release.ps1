param([Parameter(Mandatory)][string]$Directory, [string]$ReportName = 'release-check.json')
$ErrorActionPreference = 'Stop'
$releaseDirectory = [IO.Path]::GetFullPath($Directory)
$executable = Join-Path $releaseDirectory 'SwayMusic.exe'
foreach ($name in @('SwayMusic.exe','SwayMusic.dll','SwayMusic.Core.dll','Avalonia.HarfBuzz.dll','libSkiaSharp.dll','tools\yt-dlp.exe','tools\qjs.exe','LICENSE','FEATURES.md','licenses\Inter-OFL.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $releaseDirectory $name))) { throw "Missing release file: $name" }
}
if (Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File -Filter '*.pdb') { throw 'Debug symbols should not be in the release.' }
if (Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse | Where-Object { $_.Name -match '^(Metrolist.*\.(exe|dll)|SwayMusic\.(Tests|StorageTests)\.dll)$' }) { throw 'Old application or test assemblies must not be published.' }
$reportPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\artifacts\$ReportName"))
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$process = Start-Process -FilePath $executable -ArgumentList @('--verify-release', ('"{0}"' -f $reportPath)) -WorkingDirectory $releaseDirectory -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(20000)) { $process.Kill($true); throw 'The published application did not close within the verification deadline.' }
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $reportPath)) { throw "Published startup failed (exit $($process.ExitCode))." }
$result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (!$result.Opened -or !$result.CleanClose -or $result.Architecture -ne 'X64') { throw 'Published native window verification failed.' }
$result | Format-List
