# Attribution

SwayMusic is a derivative port of Metrolist by the Metrolist Project and its contributors, distributed under GNU GPL version 3. The original license is included in LICENSE. Original source: https://github.com/MetrolistGroup/Metrolist. Source used for this port is the supplied Metrolist-main directory, including its innertube module and Android app. The SwayMusic ribbon logo and wordmark are new branding for this independent Windows application; the previous Windows build used the original Metrolist icon.

Avalonia UI (MIT): https://github.com/AvaloniaUI/Avalonia

Velopack 1.2.161 (MIT): https://github.com/velopack/velopack. Its installer, updater and C# SDK package the Windows application and apply updates. Full license: licenses/Velopack-MIT.txt.

InnerTubeX (GPL-3.0), MetrolistGroup and contributors: https://github.com/MetrolistGroup/innertubex. The native visitor-backed VISIONOS_0_1 playback profile and bootstrap/fast-path approach in `YouTubeMusicClient.Playback.cs` are adapted from v0.11.2, commit c863ddf15be01a1abfa4630329bdc0a1e95f1531. The complete GPL-3.0 text is included in LICENSE. The Kotlin runtime/library is not bundled; this is a C# adaptation of the supported direct-audio path.

NAudio (MIT): https://github.com/naudio/NAudio

SkiaSharp (MIT) and Skia (BSD): https://github.com/mono/SkiaSharp

.NET runtime (MIT and third party notices shipped with the runtime): https://github.com/dotnet/runtime

Microsoft.Windows.SDK.NET / CsWinRT (MIT): https://github.com/microsoft/CsWinRT

MicroCom.Runtime (MIT): https://github.com/AvaloniaUI/MicroCom

Inter typeface (SIL Open Font License 1.1): https://rsms.me/inter/. A local font is embedded in the application; its copyright and full license are included in licenses/Inter-OFL.txt.

yt-dlp 2026.10.26 (Unlicense for original code, plus bundled components under their own licenses): https://github.com/yt-dlp/yt-dlp. The bundled executable includes Python and other libraries; their complete notices are included in tools/yt-dlp-THIRD_PARTY_LICENSES.txt. The helper runs on demand to resolve public media URLs and is not a playback engine.

QuickJS-NG 0.18.0 (MIT): https://github.com/quickjs-ng/quickjs. Its license is included in tools/quickjs-LICENSE.txt. This is a small JavaScript interpreter, not a Java runtime or browser engine.

The source of this Windows port must accompany distribution, or be made available as required by GPL-3.0. This is an independent Windows port, not an official upstream release.
