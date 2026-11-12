using System.Diagnostics;
using SwayMusic.Tests;

try
{
    if (args is ["--installed-update", var installRoot, var updateFeed]) { await InstalledUpdateTests.RunAsync(installRoot, updateFeed); return 0; }
    if (args.Contains("--lyrics-live")) { await LyricsServiceTests.LiveAsync(); return 0; }
    if (args.Contains("--orbit-live-cover")) { OrbitTests.Standalone(liveCover: true); return 0; }
    if (args.Contains("--orbit-ui")) { OrbitTests.Standalone(); return 0; }
    if (args.Contains("--orbit-audio-ui")) { OrbitTests.Standalone(audio: true); return 0; }
    if (args.Contains("--orbit-native")) { OrbitTests.Native(); return 0; }
    if (args.Contains("--artwork-live")) { ArtworkTests.Live(); return 0; }
    if (args.Contains("--artist-live")) { await ArtistTests.LiveAsync(); return 0; }
    if (args.Contains("--account-library-live")) { await AccountLibraryTests.LiveAsync(); return 0; }
    if (args.Contains("--browser-login-smoke"))
    {
        await BrowserLoginTests.SmokeAsync();
        return 0;
    }
    if (args.Contains("--browser-login-tests"))
    {
        await BrowserLoginTests.RunAsync();
        return 0;
    }
    if (args.Contains("--desktop-smoke-software"))
    {
        DesktopSmokeTests.Run(software: true);
        return 0;
    }
    if (args.Contains("--desktop-smoke"))
    {
        DesktopSmokeTests.Run();
        return 0;
    }
    if (args.Contains("--headless"))
    {
        HeadlessTests.Run();
        return 0;
    }
    if (args.Contains("--network-audio"))
    {
        await PlaybackTests.AacStreamSmokeAsync();
        await NetworkTests.LiveAsync(Path.GetFullPath("tools"));
        return 0;
    }
    var watch = Stopwatch.StartNew();
    await UpdateTests.RunAsync();
    await NetworkTests.RunAsync();
    await NativeResolverTests.RunAsync();
    await AccountNetworkTests.RunAsync();
    await AccountLibraryTests.RunAsync();
    await ArtistTests.RunAsync();
    await BrowserLoginTests.RunAsync();
    Console.WriteLine("PASS API request/authentication, renderer parsing, continuation, retry, stream validation");
    await StorageTests.RunAsync();
    await LyricsServiceTests.RunAsync();
    await AccountStorageTests.RunAsync();
    Console.WriteLine("PASS persistence, DPAPI, downloads, cache, lyrics");
    await MigrationTests.RunAsync();
    await FragmentedAacTests.RunAsync();
    Console.WriteLine("PASS fragmented AAC packet framing, sparse indexing, seeking, cancellation and bounds");
    await PlaybackTests.RunAsync();
    AudioSpectrumTests.Run();
    await PrefetchTests.RunAsync();
    Console.WriteLine("PASS queue, shuffle, repeat, cancellation, restore");
    if (args.Contains("--audio"))
    {
        await AudioSpectrumTests.NativeAsync();
        await PlaybackTests.SmokeAsync();
        await PlaybackTests.AacStreamSmokeAsync();
        await MediaControlsTests.SmokeAsync();
        Console.WriteLine("PASS native Media Foundation/WASAPI play, pause, seek, resume, stop");
    }
    if (args.Contains("--profile")) await PlaybackTests.ProfileAsync();
    if (args.Contains("--live")) await NetworkTests.LiveAsync(Path.GetFullPath("tools"));
    Console.WriteLine($"Completed in {watch.Elapsed.TotalSeconds:F2}s");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
