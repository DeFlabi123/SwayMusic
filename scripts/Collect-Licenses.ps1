$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$licenseDirectory = Join-Path $projectRoot 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
$notices = @(
    @{ Name='Velopack-MIT.txt'; Url='https://raw.githubusercontent.com/velopack/velopack/92d6a1c91716729d449034df5c50307dcce39493/LICENSE' },
    @{ Name='Avalonia-MIT.txt'; Url='https://raw.githubusercontent.com/AvaloniaUI/Avalonia/cf8e564674700348947e93ab19105f27afe859a9/licence.md' },
    @{ Name='NAudio-MIT.txt'; Url='https://raw.githubusercontent.com/naudio/NAudio/4f8889e2bd34c3978a25652c6c172fa1502ccba3/LICENSE' },
    @{ Name='MicroCom-MIT.txt'; Url='https://raw.githubusercontent.com/kekekeks/MicroCom/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE' },
    @{ Name='CsWinRT-MIT.txt'; Url='https://raw.githubusercontent.com/microsoft/CsWinRT/master/LICENSE' }
)
foreach ($notice in $notices) {
    $targetFile = Join-Path $licenseDirectory $notice.Name
    if (!(Test-Path -LiteralPath $targetFile)) { Invoke-WebRequest -Uri $notice.Url -OutFile $targetFile -TimeoutSec 30 }
}
$packageNotices = @(
    @{ Name='SkiaSharp.txt'; Path='.nuget\packages\skiasharp\3.119.4\LICENSE.txt' },
    @{ Name='HarfBuzzSharp.txt'; Path='.nuget\packages\harfbuzzsharp\8.3.1.3\LICENSE.txt' },
    @{ Name='ANGLE.txt'; Path='.nuget\packages\avalonia.angle.windows.natives\2.1.27548.20260419\LICENSE' },
    @{ Name='Skia-native-ThirdPartyNotices.txt'; Path='.nuget\packages\skiasharp.nativeassets.win32\3.119.4\THIRD-PARTY-NOTICES.txt' },
    @{ Name='HarfBuzz-native-ThirdPartyNotices.txt'; Path='.nuget\packages\harfbuzzsharp.nativeassets.win32\8.3.1.3\THIRD-PARTY-NOTICES.txt' },
    @{ Name='Numerics-Tensors-ThirdPartyNotices.txt'; Path='.nuget\packages\system.numerics.tensors\9.0.0\THIRD-PARTY-NOTICES.TXT' },
    @{ Name='dotnet-LICENSE.txt'; Path='.sdk\LICENSE.txt' },
    @{ Name='dotnet-ThirdPartyNotices.txt'; Path='.sdk\ThirdPartyNotices.txt' }
)
foreach ($notice in $packageNotices) {
    $sourceFile = Join-Path $projectRoot $notice.Path
    if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $licenseDirectory $notice.Name) -Force }
}
Get-ChildItem -LiteralPath $licenseDirectory -File | Select-Object Name,Length
