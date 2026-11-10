# Source analysis and Windows architecture

The supplied Android tree is treated as read-only. Its root AGENTS.md governs contributions to that tree; no Android changes, pulls, version changes, commits, or history operations are performed for this sibling Windows project.

## Android source map

- `settings.gradle.kts` defines Android `app` and `innertube` modules. `gradle/libs.versions.toml` pins Kotlin, Compose, Hilt, Room, Ktor, Coil, Media3, and the external InnerTubeX library. The checked-in `innertube` module wraps and maps that external API into typed pages; it does not contain every underlying request/cipher implementation.
- `app/.../MainActivity.kt`, `ui/screens`, and `viewmodels` coordinate Compose navigation and lifecycle-aware flows. `HomeViewModel.kt` builds speed dials from pinned, recent, quick-pick and home items, deduplicating IDs. `OnlineSearchViewModel.kt` maintains filter/page state, enriches missing metadata through queue requests, and filters explicit/video/Shorts results according to settings.
- `innertube/.../YouTube.kt` provides search, browse, player, recommendations, account library, playlist mutations and metadata. `pages` map responsive-list and two-row renderers; continuations are essential to large result sets. See source-network.md for the actual port and upstream investigation.
- Android Media3 ExoPlayer and a foreground music service own streaming and MediaSession controls. A desktop window lifetime and native WASAPI/Media Foundation replace the Android service/session/decoder stack. See source-playback.md.
- Room entities/DAOs persist songs, artists, albums, playlists, history, formats and lyrics; DataStore persists preferences. Android download/service and cache machinery are platform-specific. See source-storage.md for the smaller desktop persistence and disk-cache implementation.
- Lyrics providers include LRCLIB, KuGou and others, with timestamp parsing, provider fallback, translation and romanization in Android. The first Windows provider is the original LRCLIB matching algorithm with local bounded caching and synchronized line lookup.
- Android-specific integrations include widgets, Quick Settings, foreground-service notifications, Android intents, MediaRouter/Cast, and mobile permissions. Additional source features include Listen Together, Discord, Last.fm, Shazam, podcasts, Wrapped statistics and translation services. They are tracked explicitly as parity gaps rather than simulated.
- Error paths use Kotlin Result, exception reporting, coroutine handlers and network monitoring. Windows errors propagate through cancellation-aware tasks and appear in the status bar; no fake fallback content is substituted.

## Windows layers

`Metrolist.Core` has no GUI dependency. It owns immutable shared music models, shared-HttpClient API access, stream resolution, persistence, DPAPI credentials, downloads, bounded cache and lyrics.

`Metrolist.Desktop` targets Windows 10 build 19041 or later with .NET 10. It uses only the Avalonia Win32/Skia backend, Fluent controls, NAudio.Wasapi and the platform SDK. No Android, Java, Electron, browser engine or WebView is used. Composition is explicit rather than a DI container. View-model commands handle application actions; the window handles file picking, keyboard events, resize state and OS media integration.

`PlaybackService` serializes decoder operations, cancels obsolete loads, streams remote AAC through Windows Media Foundation, and renders through event-driven shared-mode WASAPI. Track transitions are decoder-driven. The queue preserves duplicates, supports shuffle without replacement, reorder, removal and repeat. A restored queue does not start the audio engine or make network requests until Play.

The UI uses compiled bindings and virtualized lists. It decodes only current artwork at 96 pixels, caps artwork responses at 2 MiB and promptly disposes previous bitmaps. The only UI playback clock runs four times per second while playing and visible; it stops when paused or minimized. No network call blocks window construction. Lyrics lookup happens only when requested. Download progress is throttled.

The library is an atomic, schema-versioned JSON snapshot. This avoids an additional database engine for the first port, with the tradeoff that library writes serialize the snapshot; a paged SQLite store would be appropriate if measured large-library loads justify it. History, lyric and audio caches are bounded. Explicit downloads are never silently evicted.

## Release choices

Provide a framework-dependent Windows folder for users with .NET 10 Desktop/runtime and a self-contained folder for machines without .NET. Both use Release compilation and workstation GC. Trimming and Native AOT remain disabled until reflection-based JSON serialization, Avalonia bindings and WinRT/COM interop are validated under those modes. Smaller unverified binaries are not a sufficient reason to sacrifice correctness.
