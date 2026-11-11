# Feature status and limitations

SwayMusic is an independent Windows port of Metrolist. Its short in-app brand is Sway. A visual redesign and rename do not imply complete Android parity. Verification evidence is recorded separately in PROGRESS.md.

## Implemented

- Native Avalonia window, compiled bindings, virtualized track lists, navigation, keyboard shortcuts and resize support. SwayMusic branding adds local Inter typography, a ribbon application icon, dark/light/system appearance, compact rows, library filtering/sorting, row context menus and an adaptive Queue/Lyrics panel.
- Source-derived YouTube Music song search and continuation, home recommendations, playlist/album/artist browse parsing, and radio queues.
- Direct AAC stream selection, actual stream availability checks, bounded signed-URL cache and on-demand official yt-dlp/QuickJS challenge resolution.
- Native Media Foundation decoding and event-driven WASAPI output; local and streamed AAC, play/pause, seeking, next/previous, ordered queue with duplicates, queue reorder/removal, shuffle and repeat, volume and background playback.
- Bounded sparse indexing and lossless packet framing of YouTube fragmented AAC for the Windows decoder; downloaded fragmented AAC plays through the same path.
- Windows System Media Transport Controls metadata, enabled transport buttons and seeking integration.
- Local playlists, liked songs, bounded listening history, queue/settings persistence and lazy paused session restore.
- Default-directory migration copies the old Metrolist library into SwayMusic, verifies staged data, retains the original as the backup and never overwrites a separate existing SwayMusic library.
- Explicit streaming-to-disk downloads, offline playback resolution, storage quotas, cancellation cleanup and atomic file commit; separately available bounded LRU audio-cache service.
- LRCLIB source-derived matching/fallback, plain and timestamped lyrics, offset/repeated-timestamp parsing, active-line lookup, bounded memory/disk lyric caches.
- Google sign-in through a temporary instance of installed Microsoft Edge or Google Chrome, followed by YouTube Music account validation and a visible account name. No browser is bundled or embedded in the native interface, and startup/playback do not launch one.
- Versioned Windows-user DPAPI account storage preserves the cookie, active account index, visitor/data-sync metadata and confirmed identity. Legacy DPAPI cookie files and Advanced manual session import remain compatible. Cancel, browser close, timeout, sign-out and shutdown dispose the owned sign-in attempt.
- Current-track artwork loading, response size limit, downsampling and disposal. Artwork is not loaded for every row.

## Remaining and restricted behavior

- Automatic Google session refresh, a native multi-account manager/channel picker, remote library synchronization and remote playlist/like mutations are not implemented. Expired/revoked cookies may require signing in again. Local likes/playlists are explicitly local.
- Google sign-in requires installed Microsoft Edge or Google Chrome and network access. The active account selected by the browser is used; completing a real Google account sign-in remains a manual check, separate from isolated protocol/UI tests.
- Extractor playback is public/unauthenticated; Premium-only, age-restricted, uploaded/private music and region-restricted playback may fail. No fake result or audio substitutes are used.
- General search filters for videos, albums, artists, podcasts, search suggestions, queue metadata enrichment and every Android browse renderer are not fully ported. Parser preserves common music renderers and reports empty/unavailable pages honestly.
- Large remote queue pagination is user-driven through Load more before playback, rather than automatically extending an active queue. Source browse section presentation is simplified.
- Automatic read-through caching during remote native playback is not connected. The bounded cache service is implemented and tested; explicit offline downloads are connected. Explicit downloads are never automatically evicted to satisfy a new download.
- Remote audio has cancellable byte-range reads with bounded body deadlines. The AAC adapter supports direct SIDX indexes and a contiguous audio run per fragment; unsupported/encrypted containers fail explicitly. A device change/unplug is reported, and opening another track uses the current default output. Codec support depends on Windows Media Foundation.
- Crossfade, gapless guarantee, ReplayGain, equalizer, silence skipping, sleep/alarm timers, casting, Listen Together, Discord, Last.fm, Shazam, Wrapped statistics, widgets, notifications/tray, translation/romanization, alternate lyrics providers and Android backup import are not yet ported.
- SMTC shell visuals and physical media-key delivery remain manual checks; native session properties are tested. Actual Win32 startup, playback updates, minimize and clean close passed an in-process native-window fixture. Mouse/keyboard behavior is also tested on the real controls under Avalonia's headless platform.
- The desktop interface is English. The language setting affects service content; Android's translated interfaces have not been ported.
- Album art appears for the current track; list art and lock-screen art are not fetched. Local filenames provide initial metadata; tag editing/scanning is not implemented.
- Trimming and Native AOT are not enabled for the shipped build. Reflection-based persistence and WinRT/COM interop require additional validation under those modes.

## Resource behavior

No network request blocks window construction. Initial Home uses local listening history until requested online. Queue restoration does not create an output device. Playback advances from native events rather than polling. The UI position timer runs at 4 Hz only while playing and visible, and stops when minimized or paused. Lyrics requests occur on opening the lyrics page. A browser starts only after the explicit Google sign-in action; none remains for ordinary idle/playback work. Software rendering and disabled control transitions reduce native graphics overhead. Shared HTTP connections, bounded caches, streaming downloads and current-only decoded artwork limit resource use. See measured values in PROGRESS.md rather than treating design intent as a benchmark.
