# Native playback URL extraction

The Windows resolver adapts the visitor-backed VISIONOS_0_1 direct-audio path from Metrolist's pinned InnerTubeX v0.11.2 (commit c863ddf15be01a1abfa4630329bdc0a1e95f1531). Source references are `extraction/InnerTubeExtractor.kt`, `extraction/strategy/PlaybackClientCatalog.kt`, `models/YouTubeClient.kt` and `extraction/YtConfigParserImpl.kt` in https://github.com/MetrolistGroup/innertubex.

The application warms anonymous visitor configuration after normal startup initialization, without blocking window construction or loading audio. One shared asynchronous gate prevents duplicate bootstrap requests. A separate cookie-free HTTP transport retains connections between selections. A small public bootstrap file under `%LOCALAPPDATA%\SwayMusic\player-cache` expires after three hours; its reader is capped at 32 KiB. No account cookie, signed media URL or executable script is persisted in this cache. The watch-page reader stops once public visitor data is found and is capped at 256 KiB.

For a selected song, a bounded native player request uses the source's client version, device metadata and visitor context. It accepts only ready HTTPS Googlevideo AAC URLs; signature-ciphered formats and URLs requiring an n transform fall back. Existing bounded HTTP-range playback and Media Foundation/WASAPI decoding are unchanged. No helper, browser, Java/Kotlin runtime or additional decoder is started on a successful native resolution.

Native extraction has an eight-second budget. Metadata/network/protocol failures return to the existing WEB_REMIX/yt-dlp path; unsupported content is not substituted with other music. Brief health cooldowns avoid repeatedly using a failing client. An actual media failure invalidates that URL and temporarily quarantines the native profile so retry can use the fallback. Cancellation remains authoritative. Normal shutdown drains initialization/preparation work before disposing its transport.

This is a port of the direct-audio fast path, shared initialization, persistent bootstrap and client-failure handling. The complete Kotlin cipher interpreter, Android WebView PoToken generator, SABR/video transports and every client-selection rule are not ported. Streams requiring those capabilities still use the helper or report an availability error. The native profile is upstream's visitor-backed legacy profile, not a stable public YouTube API; compatibility can change.

## Verification

The integrated live test resolved three different uncached songs in 300 ms, 265 ms and 325 ms, all through `native-visionos-visitor`. Cached URL retrieval took under 1 ms; subsequent audio setup took 191 ms. The previous helper-based sample required 10,561 ms for one uncached URL. These are different live samples on this host, not guaranteed per-track timings or a controlled service benchmark.

The deterministic/native/live suite passed in 42.86 seconds: native-profile/public-cookie isolation, shared and persisted configuration, corruption recovery, fallback and cancellation; account/migration/storage and native media controls; 25 seconds of real AAC streaming, pause-idle checks, seeking/resume/stop; and a full 2,484,584-byte ranged download followed by offline native decoding. Headless UI/account checks passed. Evidence is in `artifacts/native-extraction-regression.log`.
