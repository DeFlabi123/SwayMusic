# SwayMusic

**Sway** is the in-app brand; **SwayMusic** is the Windows product and executable name. This independent native C# / Avalonia music player derives from the Metrolist Android source and retains its GPL-3.0 attribution. Requires Windows 10 build 19041 or later, x64. The interface uses no Android, Java, Electron or WebView. Optional Google sign-in opens your installed Edge or Chrome in a temporary browser window.

## Run

Playback now first uses Metrolist's visitor-backed native extraction fast path. Public initialization data is warmed asynchronously and cached for three hours; different new songs reuse the same HTTP connections and configuration. The existing next-song preparation and expiry-aware URL cache remain. yt-dlp/QuickJS are retained for streams the native path cannot resolve. See `docs/native-extraction.md` for measured latency and scope.

Open `artifacts/SwayMusic-win-x64/SwayMusic.exe`. This is the single current release folder. Keep its supporting files and `tools` directory together. The release includes .NET; no installer or administrator rights are required. If an archive is explicitly requested, extract it before opening the executable.

Search YouTube Music, double-click a track to play, or open local AAC/M4A, MP3, WAV, FLAC and other Windows-supported audio. Paste a YouTube Music playlist URL into Search to browse its songs. Home loads online recommendations when selected; startup restores the local session without starting playback or blocking on the network. The queue, shuffle/repeat, volume, seeking, offline downloads, local playlists, liked songs, history and LRCLIB lyrics are available from the window. Windows media controls expose metadata and transport actions.

Use the shuffle symbol beside a collection's Play button to start at a random song. Click an artist name below a song or in the player to open their profile, with artwork and Songs, Releases & more, and About tabs. Profile details come from YouTube Music when available.

Some YouTube streams require challenge handling. The release includes checksum-verified official `yt-dlp.exe` and QuickJS-NG `qjs.exe`. They run only when direct AAC resolution cannot succeed, exit after URL resolution, and do not decode playback or remain in the background. They increase installation size and temporary resolution memory, but avoid an embedded browser and Java runtime. Updates are explicit through `scripts/Get-StreamTools.ps1`; the app never silently downloads executable updates.

To connect your YouTube Music account, open Settings and select **Sign in with Google**. Microsoft Edge or Google Chrome must be installed. Complete Google's sign-in in the browser window; Sway verifies the resulting YouTube Music account and shows its name after saving the session. Your password is entered only on Google's page. The browser runs only for this sign-in attempt and closes when the attempt finishes or is cancelled. **Cancel sign-in**, closing that window, or **Sign out** stops the attempt. Startup and ordinary playback do not launch a browser.

Saved cookies, account selection and identity metadata are encrypted with Windows DPAPI for your Windows user. Expired sessions may require signing in again. **Advanced: import a session** retains the manual Cookie-header option for compatibility; never share account cookies. The public-stream resolver does not inherit your session, so restricted/Premium-only playback is not established. Liked songs, playlists and subscribed artists now sync with the signed-in account. Existing device collections are preserved separately; see docs/account-library.md. See `docs/authentication.md` for behavior, verification status and limitations.

## Build and verify

The new Sway identity uses pear-green accents, warm dark/light surfaces and a locally bundled Inter font. Settings includes Dark, Light or System appearance and compact song rows. The optional Queue/Lyrics panel docks on wider windows; on narrower windows those actions open a full content view. Library combines local likes, playlists, listening history and downloads with filtering/sorting; per-song actions live in the row context menu. Its specification and editable vectors are in `design/SwayMusic/`. The playback/network services remain native and are shared with the previous Windows build.

Install .NET SDK 10.0.404 (or a later patch in that feature band), or run `scripts/Install-DotNet.ps1` to install a verified portable SDK in this workspace. From this directory:

```powershell
.\scripts\Get-StreamTools.ps1
.\scripts\Build.ps1 -Test -Audio -Live
.\scripts\Build.ps1 -Headless
.\scripts\Build.ps1 -Desktop
.\scripts\Publish.ps1
```

`-Test` exercises offline invariants; `-Audio` checks real Windows decoding, output, seeking and SMTC at zero volume; `-Live` checks real public YouTube Music requests, streaming and offline downloads; `-Headless` checks the Avalonia control tree without desktop input. `-Desktop` opens an isolated native window briefly, samples resources and closes it automatically. Test dependencies stay out of the release. Live tests need network access and can fail when YouTube changes or blocks the local network. Tests are an executable harness that returns nonzero on failure.

Local state is stored in `%LOCALAPPDATA%\SwayMusic`: `library.json`, DPAPI-protected account data, explicit downloads, lyrics cache, and optional audio cache. First-launch migration copies existing `%LOCALAPPDATA%\Metrolist` data without changing the original; an existing SwayMusic library is never silently overwritten. See `docs/data-migration.md` for validation and recovery details. Close the app before making a backup. Queue restoration is paused. Cancel long-running requests from the status bar.

Keyboard: Space toggles playback, Ctrl+F focuses search, Ctrl+O opens files, Ctrl+Left/Right changes songs, Left/Right seeks five seconds. The app continues playing when minimized. Closing stops playback and saves the queue.

See FEATURES.md for explicit parity gaps, PROGRESS.md for verification and measurements, and docs/architecture.md for source analysis and architecture. This port is GPL-3.0; include its source when distributing it as required by the license. Original Metrolist project and contributors retain their attribution.

Publishing updates the same `artifacts/SwayMusic-win-x64` folder and gracefully closes a running workspace instance first. Archives are created only when explicitly requested with `Publish.ps1 -Archive` or `Source-Archive.ps1`. `scripts/Verify-Release.ps1 -Directory artifacts/SwayMusic-win-x64` checks the actual published executable.
