# SwayMusic progress

Windows repository: `C:\Users\schmi\Documents\GitHub\SwayMusic`. The supplied Metrolist Android tree remains read-only and its attribution is retained.

Current packaging policy: keep one release folder, `artifacts/SwayMusic-win-x64`, containing the newest self-contained build. Duplicate releases, source/binary archives and temporary test outputs have been removed from artifacts. Publishing reuses this folder and may gracefully close the running workspace app, as authorized by the user. Earlier artifact names and test-log references below describe historical validation outputs.

## Playback startup delay

New account collection sync: paginated liked songs, playlists and subscribed artists, Artists navigation, per-account cache/refresh and outgoing likes/subscriptions/playlist edits are implemented. Core and headless fixtures pass, including account isolation and sign-out races. A read-only personal account check confirmed 9 liked songs, 3 playlists and 2 artists without making changes. See docs/account-library.md. Historical statements below about cloud sync being unimplemented describe earlier builds.

The newer native extraction path is documented in `docs/native-extraction.md`: three different uncached songs resolved in 265–325 ms, with a 42.86-second deterministic/native/live regression pass. The helper-based timings below are the earlier baseline. The direct path now shares and persists public bootstrap metadata, warms it asynchronously, and quarantines failed clients while retaining the helper fallback.

- Added preparation of one upcoming song's stream URL while the current song plays. It follows the queue's actual shuffle order, reuses an in-flight preparation when Next is pressed, and cancels unrelated work for a foreground selection. Pause, stop, account changes and shutdown cancel preparation. No decoder or whole audio file is preloaded, and session restore does not start preparation.
- Completed preparations use the existing bounded, expiry-aware stream URL cache. Speculative failures do not surface as playback errors; normal foreground resolution retries them.
- A live measurement found 10,561 ms uncached URL resolution versus under 1 ms for a cached URL, with 173 ms audio setup. This identifies URL discovery as the bottleneck in that sample; it is not a promised startup time for all tracks/networks. First-time arbitrary selections can still require resolution.
- Release compilation passed with zero warnings/errors. The deterministic/native/live suite passed in 52.61 seconds, including new queue/preparation tests, real streaming, pause/seek/resume and offline playback. Headless UI/account tests passed. Evidence: `artifacts/playback-startup-regression.log`.

## Google sign-in

- Settings now offers Sign in with Google through installed Edge/Chrome, with a unique temporary profile and private inherited CDP pipes. The browser is launched only on request and is not bundled or used as the application frontend.
- The browser supplies a YouTube Music HTTPS session and active account metadata. A separate native account-menu request must confirm the account before its cookie, visitor data, data-sync identifier, account index and identity are saved in the Windows-user DPAPI envelope. Legacy raw-cookie DPAPI files remain readable; manual import is under Advanced.
- Cancellation, browser close, timeout, sign-out and shutdown end the owned sign-in attempt. Serialized account operations and generation checks prevent a stale attempt from restoring credentials after sign-out or replacing a current account after cancellation.
- Isolated account storage tests passed legacy/envelope compatibility, metadata round trips, ciphertext-only disk contents, sign-out ordering, bounds/control-character rejection, cancellation, locked-file rollback, malformed/version/UTF8/DPAPI corruption and oversized-file recovery. The scoped Release harness built with zero warnings/errors. Core account-request fixtures have also passed.
- The final whole-solution Release build passed in 2.02 seconds with zero warnings/errors. Integrated headless tests passed account UI, cancellation/sign-out races and lifecycle checks; the Settings screenshot was visually reviewed in `docs/sway-account-login.png`.
- The final installed Microsoft Edge 157 blank-page CDP-pipe/lifecycle smoke passed in 1.10 seconds, including browser termination and removal of all owned profiles. Browser protocol/data fixtures also passed. Completing Google's real account sign-in and MFA is a manual user check and is not claimed by these tests.
- The final deterministic/native/live suite passed in 56.11 seconds (56.50 seconds wall time), including account/storage/migration fixtures, native audio and SMTC. Real public AAC played for 25.71 seconds; pause made no further upstream reads and seek/resume/stop passed. A real 1,890,654-byte ranged download then decoded and played offline without network resolution.
- Session expiry may require signing in again. Automatic refresh, native multi-account/channel selection and authenticated restricted/Premium extractor playback remain outside this implementation. Account collection sync is implemented in the newer section above.

## SwayMusic redesign

- Renamed solution, source/test projects, namespaces, executable/product metadata and release scripts to SwayMusic. The short in-app brand is Sway.
- Build and verification scripts target SwayMusic executable/assembly names. Source packaging includes the approved design specification, editable vectors and font notices, while excluding preview profiles and compiled output.
- Original Metrolist attribution and GPL-3.0 remain in distributions and documentation.
- Local Inter typography, the new ribbon icon, dark/light/system themes, compact rows, the adaptive queue/lyrics panel and data migration are implemented against the approved plan. The final Release build passed with zero warnings/errors, and final headless/native/live checks passed.
- Existing user libraries must migrate without modifying the Metrolist source directory or overwriting an existing SwayMusic library. See docs/data-migration.md for the implementation and isolated migration tests.

### SwayMusic redesign verification before browser sign-in

- The final deterministic/native/live suite passed in 52.12 seconds (52.23 seconds wall time), including API parsing/auth/ranges, persistence/rollback/DPAPI, migration, native audio and SMTC.
- Actual public YouTube AAC played for 25.71 seconds; pause made no additional upstream reads, and seeking/resume/stop passed. The real 2,484,584-byte track then downloaded via bounded ranges and played offline without invoking the URL resolver.
- The final headless suite passed appearance persistence, dark/light/System themes, compact rows, virtualized lists, queue/lyrics docking, keyboard/mouse interaction and minimum-size layout. Visual checks confirmed the olive Light-theme slider thumbs, correctly sized Home cards and the 850 px sidebar after the final style fixes. Native focus-adornment rendering is outside the headless screenshot check.
- Startup actions are disabled until initialization/migration succeeds; failed initialization exposes Retry. Explicit track selection clears any pending restored seek, and queue edits keep narrow/wide views synchronized.
- Contrast calculations against the actual theme tokens: dark primary text/window 16.76:1, secondary text/panel 7.50:1, primary action text 13.90:1; light equivalents 14.84:1, 5.27:1 and 7.22:1. Accent focus/panel contrast is 14.20:1 dark and 6.51:1 light. These establish those specific pairs, not a blanket claim about all neutral dividers.

### Current SwayMusic release artifacts

- `SwayMusic-win-x64.zip`: self-contained Windows x64 distribution with Google sign-in, approximately 154.8 MiB unpacked, including .NET 10.0.15. Its actual `SwayMusic.exe` opened and closed the native window with both runtime environment paths pointed at a nonexistent installation.
- `SwayMusic-win-x64-framework-dependent.zip`: approximately 78.1 MiB unpacked. Its actual executable opened and closed using the verified stable local .NET 10.0.15 x64 runtime.
- Both final published executables reported X64 and clean close, with window construction around 882 ms self-contained and 844 ms framework-dependent in these individual checks. Reports are included in `docs/verification-self-contained.json` and `docs/verification-framework-dependent.json`. Both distributions include the authentication documentation and reviewed account Settings screenshot.
- Newly named source and binary archives include GPL attribution, dependency notices and the Inter license; test assemblies and debug symbols are excluded from binaries. `SwayMusic-Windows-source.zip` includes the editable design specification, vector exports, fonts and verification scripts. `artifacts/SHA256SUMS.txt` lists the final archives.

The older feature, verification, performance and artifact entries farther below are retained as the **previous Windows build baseline**.

### SwayMusic native-window performance sample

The redesigned native Win32 fixture passed real window construction, visible/minimized silent WAV playback, pause and clean close from an isolated output copy. The first new-path startup sample was 2,509 ms; a warm same-path rerun was 1,098 ms. Both are single observations, not cold-start guarantees.

| Redesigned native application state | Host CPU, observed range | Private memory | Working set |
| --- | ---: | ---: | ---: |
| Visible idle | ~0.032% | ~47–48 MiB | ~115–116 MiB |
| Visible local WAV playing | ~1.17–1.23% | ~51 MiB | ~130–131 MiB |
| Visible paused | ~0.065% | ~51 MiB | ~130–131 MiB |
| Minimized local WAV playing | ~0.065–0.098% | ~51 MiB | ~130–131 MiB |
| Minimized paused | ~0.065% | ~51 MiB | ~130–131 MiB |

The richer shell and bundled font add about 6 MiB of private idle memory compared with the old baseline. Warm startup and CPU remain comparable in these short samples. These samples preceded the final small sidebar/input fixes and must not be presented as a guarantee that every workload has no performance regression.

## Previous build: implemented and verified

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

## Previous build: verification evidence

- Full deterministic/native/live suite passed in 50.22 seconds: API/auth/retry fixtures, parsing, sparse ranges, storage/DPAPI/rollback, downloads/cache/lyrics, fragmented AAC framing/seeking/bounds, queue cancellation/restore, real WAV/AAC output and SMTC.
- Actual public YouTube AAC played over 25 seconds; 581,632 of 2,484,584 source bytes fetched (~23%). A two-second pause made zero upstream reads. Seek to ten seconds, resume and stop passed.
- The same actual track downloaded completely through ranges and played offline for three seconds with pause/seek/resume/stop; its resolver throws on any network attempt.
- Headless production-control checks passed mouse/keyboard navigation, settings/playlists, concurrent account actions, interrupted startup and resizing. A 4,096-song list realized six row controls. docs/screenshot.png is a clean production view without fixture music/playlists.
- Native Win32 fixture opens an isolated production window without taking focus, exercises silent audio, samples resources, minimizes/pauses and closes cleanly. User library is untouched.

## Previous build: performance measurements

Short samples on a 16-logical-processor Windows host, not universal minima. Startup with warm filesystem caches: about 1.0–1.1 seconds to window-open. Software rendering reduced private idle memory from about 112 MiB to 41 MiB and threads from 64 to 17–18 compared with accelerated rendering.

| Native application state | Host CPU | Private memory | Working set |
| --- | ---: | ---: | ---: |
| Visible idle | 0.000% | ~41 MiB | ~107 MiB |
| Visible local WAV playing | ~1.1–1.2% | ~45 MiB | ~123 MiB |
| Visible paused | ~0.13–0.16% | ~45 MiB | ~123 MiB |
| Minimized local WAV playing | ~0.19% | ~46 MiB | ~124 MiB |
| Minimized paused | ~0.03–0.19% | ~45–46 MiB | ~123–124 MiB |

Private committed memory and resident working set differ. These include UI/fonts/SMTC; engine-only numbers in docs/source-playback.md exclude UI. Three-second samples, JIT and caches affect results. The transient extractor is absent during these samples. Visible playback updates four times per second; minimized playback stops UI clock updates.

## Previous build: release artifacts

- Self-contained win-x64 folder and ZIP produced: 152.72 MiB unpacked, including .NET 10.0.15. The actual published executable opened/closed its x64 native window successfully with runtime environment paths deliberately pointed to a nonexistent installation.
- Framework-dependent win-x64 folder and ZIP produced: 76.02 MiB unpacked. Its actual executable opened/closed successfully against the host's installed .NET 10.0.9 x64 runtime.
- Test assemblies and debug symbols are excluded. GPL-3.0, original attribution, dependency licenses and pinned helper notices are included.
- Corresponding source archive and SHA256 checksum manifest accompany the binaries. Build, publish, source packaging and actual-executable verification scripts are included in the source.

## Remaining and known limitations

Complete Android parity is not claimed. FEATURES.md tracks remaining collection parity, automatic Google session refresh, native multi-account/channel selection, restricted/Premium playback, automatic disk read-through caching, additional search/providers/integrations, translated UI, effects/casting/social features and AOT/trimming. Existing device playlists/likes remain local; signed-in account collections sync separately. Physical media-key delivery and Windows shell visuals remain manual checks. Unsupported/encrypted AAC containers fail explicitly; supported structures are documented in docs/source-playback.md.

No Android file was modified, upstream version changed, or commit/push/merge performed. GPL-3.0 attribution and dependency notices accompany source/release.
