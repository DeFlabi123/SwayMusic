# Metrolist for Windows

An independent native C# / Avalonia port of the supplied Metrolist Android source. Requires Windows 10 build 19041 or later, x64. There is no Android, Java, browser engine, Electron or WebView frontend.

## Run

Extract the portable ZIP and open `Metrolist.exe`. From the source workspace, the executable is in `artifacts/Metrolist-win-x64/`. Keep its supporting files and `tools` directory together. The self-contained release includes .NET; the smaller framework-dependent release requires the .NET 10 x64 runtime. No installer or administrator rights are required to run the app.

Search YouTube Music, double-click a track to play, or open local AAC/M4A, MP3, WAV, FLAC and other Windows-supported audio. Paste a YouTube Music playlist URL into Search to browse its songs. Home loads online recommendations when selected; startup restores the local session without starting playback or blocking on the network. The queue, shuffle/repeat, volume, seeking, offline downloads, local playlists, liked songs, history and LRCLIB lyrics are available from the window. Windows media controls expose metadata and transport actions.

Some YouTube streams require challenge handling. The release includes checksum-verified official `yt-dlp.exe` and QuickJS-NG `qjs.exe`. They run only when direct AAC resolution cannot succeed, exit after URL resolution, and do not decode playback or remain in the background. They increase installation size and temporary resolution memory, but avoid an embedded browser and Java runtime. Updates are explicit through `scripts/Get-StreamTools.ps1`; the app never silently downloads executable updates.

Account cookies can be imported from the Cookie request header of your own signed-in YouTube Music browser session in Settings. The app encrypts them with Windows DPAPI and does not display a login WebView. Do not share account cookies. Account API requests use the original SAPISID authorization scheme. The external public-stream resolver does not inherit your cookies; restricted/Premium-only playback is not established.

## Build and verify

Install .NET SDK 10.0.404 (or a later patch in that feature band), or run `scripts/Install-DotNet.ps1` to install a verified portable SDK in this workspace. From this directory:

```powershell
.\scripts\Get-StreamTools.ps1
.\scripts\Build.ps1 -Test -Audio -Live
.\scripts\Build.ps1 -Headless
.\scripts\Build.ps1 -Desktop
.\scripts\Publish.ps1
.\scripts\Publish.ps1 -FrameworkDependent
```

`-Test` exercises offline invariants; `-Audio` checks real Windows decoding, output, seeking and SMTC at zero volume; `-Live` checks real public YouTube Music requests, streaming and offline downloads; `-Headless` checks the Avalonia control tree without desktop input. `-Desktop` opens an isolated native window briefly, samples resources and closes it automatically. Test dependencies stay out of the release. Live tests need network access and can fail when YouTube changes or blocks the local network. Tests are an executable harness that returns nonzero on failure.

Local state is stored in `%LOCALAPPDATA%\Metrolist`: `library.json`, DPAPI-protected account data, explicit downloads, lyrics cache, and optional audio cache. Close the app before making a backup. Queue restoration is paused. Cancel long-running requests from the status bar.

Keyboard: Space toggles playback, Ctrl+F focuses search, Ctrl+O opens files, Ctrl+Left/Right changes songs, Left/Right seeks five seconds. The app continues playing when minimized. Closing stops playback and saves the queue.

See FEATURES.md for explicit parity gaps, PROGRESS.md for verification and measurements, and docs/architecture.md for source analysis and architecture. This port is GPL-3.0; include its source when distributing it as required by the license. Original Metrolist project and contributors retain their attribution.
