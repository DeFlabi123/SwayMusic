using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SwayMusic.Desktop.Updates;
using Velopack.Locators;

namespace SwayMusic.Tests;

// Explicit opt-in fixture. Never used by ordinary tests or the production app.
public static class InstalledUpdateTests
{
    public static async Task RunAsync(string root, string feed)
    {
        root = Path.GetFullPath(root); feed = Path.GetFullPath(feed);
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)
            || !feed.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer verification must use isolated artifacts directories.");
        string executable = Path.Combine(root, "current", "SwayMusic.exe");
        string initial = FileVersionInfo.GetVersionInfo(executable).ProductVersion!.Split('+')[0];
        using var handler = new LocalReleaseHandler(feed);
        var locator = new TrackedInstallLocator(root, initial);
        using var service = new VelopackUpdateService(locator, handler);
        await service.InitializeAsync(CancellationToken.None);
        await service.CheckAsync(CancellationToken.None);
        if (!service.Status.CanDownload) throw new InvalidOperationException(service.Status.Message);
        await service.DownloadAsync(CancellationToken.None);
        if (!service.Status.CanInstall) throw new InvalidOperationException(service.Status.Message);
        await service.ValidatePendingAsync(CancellationToken.None);
        service.ApplyAfterExit(restart: false);
        using var updateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var updater = locator.Runner.Started ?? throw new InvalidOperationException("The updater was not launched.");
        await updater.WaitForExitAsync(updateDeadline.Token);
        if (updater.ExitCode != 0) throw new InvalidOperationException("Update.exe failed with exit code " + updater.ExitCode);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(250);
            try
            {
                if (File.Exists(executable) && FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0] == handler.Version)
                {
                    Console.WriteLine($"PASS installed update: {initial} -> {handler.Version} through signed discovery, download, verification and Update.exe apply");
                    return;
                }
            }
            catch (IOException) { }
        }
        throw new TimeoutException("The installed application's version did not advance.");
    }

    private sealed class TrackedInstallLocator(string root, string version) : TestVelopackLocator(
        SignedUpdateRelease.AppId, version, Path.Combine(root, "packages"), Path.Combine(root, "current"),
        root, Path.Combine(root, "Update.exe"), SignedUpdateRelease.StableChannel)
    {
        public TrackingProcess Runner { get; } = new();
        public override IProcessImpl Process => Runner;
    }

    private sealed class TrackingProcess : IProcessImpl
    {
        public Process? Started { get; private set; }
        public string GetCurrentProcessPath() => Environment.ProcessPath!;
        // The harness lives outside the installation being updated, so no application process needs waiting on.
        public uint GetCurrentProcessId() => 0;
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            var start = new ProcessStartInfo(exePath) { WorkingDirectory = workDir, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in args) start.ArgumentList.Add(argument);
            Started = System.Diagnostics.Process.Start(start);
        }
        public void Exit(int exitCode) => throw new InvalidOperationException("The fixture must not terminate the test harness.");
    }

    private sealed class LocalReleaseHandler : HttpMessageHandler
    {
        private readonly string _feed;
        private readonly string[] _assets;
        public string Version { get; }
        public LocalReleaseHandler(string feed)
        {
            _feed = feed;
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(feed, SignedUpdateRelease.ManifestName)));
            Version = manifest.RootElement.GetProperty("version").GetString()!;
            _assets = [SignedUpdateRelease.ManifestName, SignedUpdateRelease.SignatureName, manifest.RootElement.GetProperty("fileName").GetString()!];
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content;
            if (request.RequestUri!.Host == "api.github.com")
                content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[] { new
                {
                    tag_name = "v" + Version, draft = false, prerelease = false,
                    assets = _assets.Select(name => new { name, browser_download_url = SignedGithubSource.Repository + "/releases/download/v" + Version + "/" + name })
                } }));
            else
            {
                string name = Path.GetFileName(request.RequestUri.AbsolutePath);
                if (!_assets.Contains(name)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                content = new StreamContent(File.OpenRead(Path.Combine(_feed, name)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
