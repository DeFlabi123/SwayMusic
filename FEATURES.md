# Feature status and limitations

This is an independent Windows port. A working native music player does not imply complete Android parity. Verification evidence is recorded separately in PROGRESS.md.

## Implemented

- Native Avalonia window, compiled bindings, virtualized track lists, navigation, keyboard shortcuts and resize support.
- Source-derived YouTube Music song search and continuation, home recommendations, playlist/album/artist browse parsing, and radio queues.
- Direct AAC stream selection, actual stream availability checks, bounded signed-URL cache and on-demand official yt-dlp/QuickJS challenge resolution.
- Native Media Foundation decoding and event-driven WASAPI output; local and streamed AAC, play/pause, seeking, next/previous, ordered queue with duplicates, queue reorder/removal, shuffle and repeat, volume and background playback.
- Bounded sparse indexing and lossless packet framing of YouTube fragmented AAC for the Windows decoder; downloaded fragmented AAC plays through the same path.
- Windows System Media Transport Controls metadata, enabled transport buttons and seeking integration.
- Local playlists, liked songs, bounded listening history, queue/settings persistence and lazy paused session restore.
- Explicit streaming-to-disk downloads, offline playback resolution, storage quotas, cancellation cleanup and atomic file commit; separately available bounded LRU audio-cache service.
- LRCLIB source-derived matching/fallback, plain and timestamped lyrics, offset/repeated-timestamp parsing, active-line lookup, bounded memory/disk lyric caches.
- Cookie-based account API authorization and Windows-user DPAPI protection; no login WebView.
- Current-track artwork loading, response size limit, downsampling and disposal. Artwork is not loaded for every row.

## Remaining and restricted behavior

- Full Google sign-in/refresh flows, multiple accounts, account validation UI, remote library synchronization and remote playlist/like mutations are not implemented. Local likes/playlists are explicitly local.
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

No network request blocks window construction. Initial Home uses local listening history until requested online. Queue restoration does not create an output device. Playback advances from native events rather than polling. The UI position timer runs at 4 Hz only while playing and visible, and stops when minimized or paused. Lyrics requests occur on opening the lyrics page. Software rendering and disabled control transitions reduce native graphics overhead. Shared HTTP connections, bounded caches, streaming downloads and current-only decoded artwork limit resource use. See measured values in PROGRESS.md rather than treating design intent as a benchmark.
