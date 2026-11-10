# Playback source analysis and Windows adaptation

Original source was read without modification in `../Metrolist/Metrolist-main`.

`app/src/main/kotlin/com/metrolist/music/playback/MusicService.kt` owns the Android Media3 ExoPlayer, Android media session, audio focus/noisy-output handling, queue restoration, volume/repeat/shuffle preferences, download and streaming data sources, playback error recovery, and history reporting. `PlayerConnection.kt` exposes player state to Compose. Android services, notifications, wake locks, audio focus, and Media3 cannot be reused on Windows.

`playback/queues/Queue.kt` defines initial queue items, selected index and position plus pagination. `ListQueue.kt` is the finite queue implementation. YouTube queue classes obtain tracks through Innertube next/continuation responses. The desktop finite queue preserves selected entries and duplicate songs, applies Fisher-Yates shuffle without copying track metadata, repeats one only on natural completion, and supports previous/repeat-all wrapping. Remote continuation loading belongs to the network/UI layer.

`StreamUrlCache.kt` bounds resolved URL entries, checks expiry, carries request headers/client information, and uses generations to reject stale resolution results. The Windows client owns URL resolution and expiry; playback accepts a resolver function and local paths. This keeps provider and cache logic outside the audio engine.

`MusicService.kt` chooses downloaded resources before remote resolution through `createCacheDataSource` and `ResolvingDataSource`; it recovers player errors by invalidating stream/cache entries and re-resolving URLs. The desktop resolver similarly checks offline paths first. Windows playback reports concrete native decoder/device/network errors rather than claiming a song is playing after failure.

## Native engine

The application references `NAudio.Wasapi` 3.3.0 and its small `NAudio.Core` dependency, without MIDI, ASIO, WinMM or WinForms packages. Media Foundation decodes AAC/MP4, MP3, WAV and other installed codecs incrementally. The `WasapiPlayer` uses shared output and event synchronization with a conservative 200 ms buffer. Decoding/opening and seeking run off the UI thread. Playback has no application polling task or timer. Pause suspends native rendering; disposal releases the output before its decoder.

Remote audio uses the application's shared `HttpClient` through `HttpRangeStream`, retaining one 256 KiB compressed chunk. Real YouTube AAC uses fragmented DASH MP4, which Windows' native MP4 source returned as immediate end-of-stream even when the exact file was downloaded locally. A raw `IMFSourceReader.ReadSample` probe confirmed that no PCM sample was supplied; this was a container issue rather than audio output or lost flags.

`FragmentedAacStream` reads the MP4 movie configuration, SIDX segment offsets and per-segment `tfhd`/`trun` sample sizes. It exposes the unchanged compressed AAC packets with seven-byte ADTS headers to Windows' native AAC decoder. This is packet reframing, not transcoding. Metadata requests are limited to four concurrent sparse reads, normally 4 KiB per fragment. Payload remains on demand in the existing compressed chunk. One cumulative integer offset per frame supports exact virtual byte seeking; the complete frame index is capped at 16 MiB (roughly 620 KiB for one hour at 44.1 kHz). Movie/fragment metadata, nesting, segment/sample counts and packet bounds are validated.

The same detection and adapter apply to local downloaded fragmented AAC files. Ordinary local audio keeps its existing native file path, and progressive remote MP4 uses the native byte-stream reader. The previous experimental loopback HTTP server was removed after the direct ADTS stream passed real playback; no audio server or additional decoder process remains running.

Cancellation supersedes older resolver requests. Track transitions serialize through one asynchronous gate, avoiding concurrent decoder replacement. Completion events advance the queue; explicit stop detaches the completion callback before teardown. The UI separately controls any visible progress updates and must stop them when inactive.

`WindowsMediaControls` attaches to the application's actual HWND and exposes play, pause, stop, next, previous and seek through Windows System Media Transport Controls. Metadata and timeline refresh on state changes. It uses the Windows SDK projection already required by the Windows target; no extra media process or hidden player is created.

## Explicit limitations

- Direct Media Foundation cloud URL opening stalled in live verification. The replacement HTTP transport has bounded body timeouts and explicit cancellation. Windows then returned EOS for the actual fragmented MP4 even from local disk, so fragmented AAC is reframed as ADTS before native decoding. Per-session cancellation stops real range requests before output, decoder and source disposal.
- Fragmented AAC adaptation requires a direct SIDX index, a supported AAC AudioSpecificConfig and one contiguous audio sample run per fragment. Hierarchical segment indexes, encrypted streams and arbitrary fragmented video containers are not supported by this small audio adapter. Unsupported structures fail explicitly.
- The remote decoder is configured for AAC/MP4. WebM/Opus availability depends on installed Windows codecs and is not assumed. Provider-specific HTTP headers belong to the injected transport factory; no cookie is attached to unrelated audio hosts by the engine.
- The initial native output is bound to the default device at opening. Automatic routing to a newly selected or unplugged device is not enabled; a new track or restart reopens the current default. Native output failure is reported.
- Crossfade, replay gain/normalization, equalizer, silence skipping, Android audio focus/ducking, sleep timers and alarm playback are not ported by this engine.
- There is no claim of gapless decoding between separately opened tracks. There is no preloaded secondary decoder, reducing memory and idle work.
- Lock-screen artwork is not fetched by this integration; track title, artist and album are supplied.

## Verification

`PlaybackTests.RunAsync()` tests selected indices, duplicate entries, shuffle permutation, repeat-one/manual-next distinction, repeat-all wrapping, remove/reorder, safe empty queues, lazy restoration and cancellation of superseded asynchronous resolution. `PlaybackTests.SmokeAsync()` writes a temporary silent WAV and checks actual Media Foundation decoding and WASAPI play, pause, seek, resume, stop, automatic queue advancement and natural final completion at volume zero.

On 2026-11-09 both test methods passed on this Windows host in an isolated playback probe compiled against the production playback files and NAudio.Wasapi 3.3.0. Actual native audio device access, silent WAV decoding, seeking and event-driven queue completion were exercised successfully. YouTube streaming and SMTC UI interaction are separate validations, not established by the local audio test.

`MediaControlsTests.SmokeAsync()` also passed: a genuine hidden native window was created, the production SMTC integration was attached, and the actual Windows media session for that HWND returned the expected title, artist, album, stopped state, and enabled transport buttons. Physical media-key delivery and shell visuals were not checked by this test.

On 2026-11-10 the final direct ADTS adapter passed a real public YouTube Music AAC stream for 25 seconds through Media Foundation and WASAPI. The playback clock advanced from 0.21 to 25.21 seconds, only 581,632 bytes of the 2,484,584-byte track had been fetched, and a two-second pause made no additional upstream reads. Seeking to ten seconds, resuming and stopping also passed at zero output volume. This establishes incremental compressed streaming rather than a full-file download before playback.

`FragmentedAacTests.RunAsync()` exercises a synthetic two-fragment MP4 with forty variable-size AAC packet payloads. It verifies metadata-only indexing, every generated ADTS header and unchanged payload byte, exact virtual length/frame duration, seeks across headers/frames/fragments, EOF, cancellation and malformed sample-count rejection. The fixture contains no real music and is never used by production features. `AacStreamSmokeAsync()` separately encodes a short silent AAC file through Windows and exercises actual native decoding, seek/resume and transport lifetime.

`PlaybackTests.ProfileAsync()` measured the standalone native audio engine process over 3-second intervals on this 16-logical-processor host. These values exclude Avalonia and are local silent WAV measurements, not AAC/network playback benchmarks:

| State | CPU, one-core percent | CPU, host percent | Private MiB | Working set MiB | Managed heap MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Engine idle | 0.000 | 0.000 | 8.70 | 28.82 | 0.71 |
| Local WAV playing | 7.796 | 0.487 | 13.34 | 40.54 | 0.85 |
| Paused | 0.520 | 0.033 | 13.34 | 40.58 | 0.86 |
| Engine disposed | 0.000 | 0.000 | 9.35 | 36.56 | 0.87 |

Short sampling windows, JIT warmup and driver work affect CPU readings. Memory reclamation follows Windows working-set policy; native private memory dropped by roughly 4 MiB on disposal without forcing a GC. No claim is made that these are minimum possible values.

Official implementation references checked 2026-11-09: [NAudio 3.3.0](https://www.nuget.org/packages/NAudio.Wasapi/3.3.0), [WasapiPlayer](https://naudio.github.io/NAudio/docs/WasapiPlayer.html), [MediaFoundationReader API](https://naudio.github.io/NAudio/api/NAudio.Wave.MediaFoundationReader.html), [Windows SDK desktop interop](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-com-interop-csharp), and [SMTC GetForWindow](https://learn.microsoft.com/en-us/windows/win32/api/systemmediatransportcontrolsinterop/nf-systemmediatransportcontrolsinterop-isystemmediatransportcontrolsinterop-getforwindow).
