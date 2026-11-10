# Metrolist Windows progress

Updated 2026-11-10. Windows project: C:\Users\schmi\Desktop\Metrolist pc. The supplied Android tree remains read-only.

## Implemented and verified

- Stable .NET SDK 10.0.404 installed from Microsoft's official archive and SHA512 verified; runtime 10.0.15. Prerelease SDK selection disabled. Release builds have zero warnings/errors.
- Native Avalonia Win32/Skia/HarfBuzz UI, compiled bindings, navigation, keyboard shortcuts, resizing/minimize and clean close. Icons derive from original Metrolist assets.
- Source-derived YouTube Music search, pagination, Home, playlist/album browse and radio; all five real public API paths passed. No fixture data appears in production.
- Real AAC streaming through shared HTTP connections, bounded ranges, expiring URL cache and checksum-verified on-demand yt-dlp/QuickJS resolution. No Android, Java, browser/WebView or external audio decoder process.
- YouTube fragmented AAC is indexed through sparse metadata and exposed as virtual ADTS to Windows' decoder. Compressed packets remain unchanged. Payload buffer: 256 KiB; frame index bounded at 16 MiB.
- Native Media Foundation/WASAPI playback, seeking, next/previous, queue duplicates/reorder/removal, shuffle/repeat, volume and background playback. SMTC metadata/session properties tested on a real HWND.
- Real ranged download and native offline playback verified without calling the URL resolver. Quotas, cancellation, atomic commit and a separate bounded LRU cache service are tested.
- Local playlist CRUD, likes, bounded history, settings/queue persistence, corruption recovery and rollback. DPAPI credentials, concurrent save/sign-out and close-during-startup regressions passed.
- LRCLIB matching/fallback, bounded lyrics caches, synchronized LRC parsing/lookup. Provider availability for individual songs remains dependent on LRCLIB.
- Current-only bounded/downsampled artwork. Playback clock stops paused/minimized. Control transitions disabled; software rendering selected from measurements.

## Verification evidence

- Full deterministic/native/live suite passed in 50.22 seconds: API/auth/retry fixtures, parsing, sparse ranges, storage/DPAPI/rollback, downloads/cache/lyrics, fragmented AAC framing/seeking/bounds, queue cancellation/restore, real WAV/AAC output and SMTC.
- Actual public YouTube AAC played over 25 seconds; 581,632 of 2,484,584 source bytes fetched (~23%). A two-second pause made zero upstream reads. Seek to ten seconds, resume and stop passed.
- The same actual track downloaded completely through ranges and played offline for three seconds with pause/seek/resume/stop; its resolver throws on any network attempt.
- Headless production-control checks passed mouse/keyboard navigation, settings/playlists, concurrent account actions, interrupted startup and resizing. A 4,096-song list realized six row controls. docs/screenshot.png is a clean production view without fixture music/playlists.
- Native Win32 fixture opens an isolated production window without taking focus, exercises silent audio, samples resources, minimizes/pauses and closes cleanly. User library is untouched.

## Performance measurements

Short samples on a 16-logical-processor Windows host, not universal minima. Startup with warm filesystem caches: about 1.0–1.1 seconds to window-open. Software rendering reduced private idle memory from about 112 MiB to 41 MiB and threads from 64 to 17–18 compared with accelerated rendering.

| Native application state | Host CPU | Private memory | Working set |
| --- | ---: | ---: | ---: |
| Visible idle | 0.000% | ~41 MiB | ~107 MiB |
| Visible local WAV playing | ~1.1–1.2% | ~45 MiB | ~123 MiB |
| Visible paused | ~0.13–0.16% | ~45 MiB | ~123 MiB |
| Minimized local WAV playing | ~0.19% | ~46 MiB | ~124 MiB |
| Minimized paused | ~0.03–0.19% | ~45–46 MiB | ~123–124 MiB |

Private committed memory and resident working set differ. These include UI/fonts/SMTC; engine-only numbers in docs/source-playback.md exclude UI. Three-second samples, JIT and caches affect results. The transient extractor is absent during these samples. Visible playback updates four times per second; minimized playback stops UI clock updates.

## Release artifacts

- Self-contained win-x64 folder and ZIP produced: 152.72 MiB unpacked, including .NET 10.0.15. The actual published executable opened/closed its x64 native window successfully with runtime environment paths deliberately pointed to a nonexistent installation.
- Framework-dependent win-x64 folder and ZIP produced: 76.02 MiB unpacked. Its actual executable opened/closed successfully against the host's installed .NET 10.0.9 x64 runtime.
- Test assemblies and debug symbols are excluded. GPL-3.0, original attribution, dependency licenses and pinned helper notices are included.
- Corresponding source archive and SHA256 checksum manifest accompany the binaries. Build, publish, source packaging and actual-executable verification scripts are included in the source.

## Remaining and known limitations

Complete Android parity is not claimed. FEATURES.md tracks remote account/library/playlist synchronization, full Google sign-in, restricted/Premium playback, automatic disk read-through caching, additional search/providers/integrations, translated UI, effects/casting/social features and AOT/trimming. Local playlists/likes remain local. Physical media-key delivery and Windows shell visuals remain manual checks. Unsupported/encrypted AAC containers fail explicitly; supported structures are documented in docs/source-playback.md.

No Android file was modified, upstream version changed, or commit/push/merge performed. GPL-3.0 attribution and dependency notices accompany source/release.
