# SwayMusic brand assets

- `src/SwayMusic.Desktop/Assets/sway.ico`: native Windows icon with 16, 20, 24, 32, 48, 64, 128 and 256 px PNG frames.
- `src/SwayMusic.Desktop/Assets/sway.png`: transparent-edged 256 px app/window image.
- `sway-symbol.svg`: pear rounded-square S ribbon; monochrome and reversed symbols are also supplied.
- `sway-wordmark-outlined-light.svg` / `sway-wordmark-outlined-dark.svg`: font-independent outlined lockups for dark and light surfaces. The wordmark is Inter Semibold 600 with compact tracking, the MUSIC label Inter Medium 500 with open tracking.
- `brand-size-preview.png`: taskbar-size review sheet. The smallest marks are shown at native pixel dimensions; larger marks are displayed at 64 px for comparison.

The production font set is the official Inter 4.1 static Regular 400, Medium 500 and Semibold 600 files from the [Inter 4.1 release](https://github.com/rsms/inter/releases/tag/v4.1). Total font payload is approximately 1.19 MiB; no network font request is needed at runtime. The SIL Open Font License is included beside the fonts and at `licenses/Inter-OFL.txt`.

Avalonia font-family resource URI: `avares://SwayMusic/Assets/Fonts#Inter` (the assembly name is SwayMusic). Use `FontWeight="Normal"`, `Medium` or `SemiBold` with this family.

The symbol is code-native vector artwork using the approved pear #D6F68A and ink #152019. To regenerate raster/ICO and outlined exports after package restore:

```powershell
.\.sdk\dotnet.exe run --project design\SwayMusic\AssetBuilder\AssetBuilder.csproj -c Release -- (Get-Location).Path
```

The generator uses the application's existing SkiaSharp library through build-time references; it adds no application dependency. Existing Metrolist attribution remains in the app's license/about files.
