using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SwayMusic.Desktop.Authentication;

/// <summary>A temporary, visible installed browser supplies a YouTube Music session to the native client.</summary>
public sealed class BrowserLoginService : IBrowserLoginService
{
    private const string SignInUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fmusic.youtube.com%2F";
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    public async Task<BrowserLoginResult> SignInAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Browser sign-in requires Windows.");
        await SignInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwayMusic", "BrowserLogin");
                await using var browser = await BrowserLoginSession.StartAsync(root, SignInUrl, headless: false, deadline.Token).ConfigureAwait(false);
                progress?.Report($"Sign in to Google in {browser.BrowserName}. Close that window or select Cancel to stop.");
                string? attachedTarget = null, sessionId = null;
                bool observedPage = false;
                var initialPageDeadline = Stopwatch.StartNew();
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    try
                    {
                    var targets = await browser.CommandAsync("Target.getTargets", cancellationToken: deadline.Token).ConfigureAwait(false);
                    if (!targets.TryGetProperty("targetInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("The sign-in browser did not provide its windows.");
                    var pages = infos.EnumerateArray().Where(t => Text(t, "type") == "page").ToArray();
                    if (pages.Length == 0)
                    {
                        if (observedPage) throw new OperationCanceledException("The sign-in browser was closed.", deadline.Token);
                        if (initialPageDeadline.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("The sign-in browser did not open its window. Please try again.");
                        await Task.Delay(200, deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    observedPage = true;
                    JsonElement? music = pages.Cast<JsonElement?>().FirstOrDefault(t => BrowserLoginData.IsMusicUrl(Text(t!.Value, "url")));
                    if (music is { } target)
                    {
                        string? targetId = Text(target, "targetId");
                        if (targetId is not null)
                        {
                            if (attachedTarget != targetId)
                            {
                                var attached = await browser.CommandAsync("Target.attachToTarget", new() { ["targetId"] = targetId, ["flatten"] = true }, cancellationToken: deadline.Token).ConfigureAwait(false);
                                sessionId = Text(attached, "sessionId");
                                attachedTarget = targetId;
                            }
                            if (sessionId is not null)
                            {
                                var evaluated = await browser.CommandAsync("Runtime.evaluate", new()
                                {
                                    ["expression"] = BrowserLoginData.MetadataExpression,
                                    ["returnByValue"] = true,
                                    ["silent"] = true
                                }, sessionId, deadline.Token).ConfigureAwait(false);
                                if (evaluated.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                                {
                                    var stored = await browser.CommandAsync("Storage.getCookies", cancellationToken: deadline.Token).ConfigureAwait(false);
                                    if (stored.TryGetProperty("cookies", out var cookies) && BrowserLoginData.CookieHeader(cookies, DateTimeOffset.UtcNow) is { } header &&
                                        BrowserLoginData.Result(value, header, browser.BrowserName) is { } login)
                                    {
                                        progress?.Report("Google sign-in completed. Checking your YouTube Music account…");
                                        return login;
                                    }
                                }
                            }
                        }
                    }
                    else { attachedTarget = null; sessionId = null; }
                    }
                    catch (CdpCommandException error) when (error.IsNavigationFailure)
                    {
                        // Google redirects cross-process during sign-in. Only known target/context
                        // destruction is retryable; unsupported methods remain visible failures.
                        attachedTarget = null; sessionId = null;
                    }
                    await Task.Delay(700, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException("Google sign-in timed out. Select Sign in with Google to try again.");
            }
            catch (IOException error) when (error is not BrowserProfileCleanupException && !cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The sign-in browser was closed.");
            }
        }
        finally { SignInGate.Release(); }
    }

    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}

internal sealed class BrowserLoginSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _profileRoot;
    private readonly string _profile;
    private readonly CdpPipeConnection _connection;
    private int _disposed;
    public string BrowserName { get; }
    internal string ProfileDirectory => _profile;
    internal int ProcessId => _process.Id;

    private BrowserLoginSession(Process process, string profileRoot, string profile, CdpPipeConnection connection, string browserName)
    {
        _process = process; _profileRoot = profileRoot; _profile = profile; _connection = connection; BrowserName = browserName;
    }

    internal static async Task<BrowserLoginSession> StartAsync(string profileRoot, string url, bool headless, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = InstalledBrowsers().ToArray();
        if (installed.Length == 0) throw new InvalidOperationException("Install Microsoft Edge or Google Chrome to sign in with Google.");
        Exception? lastError = null;
        foreach (var (executable, name) in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(profileRoot);
            string profile = Path.Combine(root, "signin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            var toBrowser = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var fromBrowser = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            Process? process = null;
            BrowserLoginSession? session = null;
            try
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = headless };
                start.ArgumentList.Add("--user-data-dir=" + profile);
                start.ArgumentList.Add("--remote-debugging-pipe");
                start.ArgumentList.Add("--remote-debugging-io-pipes=" + toBrowser.GetClientHandleAsString() + "," + fromBrowser.GetClientHandleAsString());
                start.ArgumentList.Add("--no-first-run");
                start.ArgumentList.Add("--no-default-browser-check");
                start.ArgumentList.Add("--disable-background-mode");
                start.ArgumentList.Add("--disable-sync");
                if (headless)
                {
                    start.ArgumentList.Add("--headless=new");
                    start.ArgumentList.Add("--disable-background-networking");
                }
                else start.ArgumentList.Add("--new-window");
                start.ArgumentList.Add(url);
                process = Process.Start(start) ?? throw new InvalidOperationException("The sign-in browser could not be started.");
                toBrowser.DisposeLocalCopyOfClientHandle();
                fromBrowser.DisposeLocalCopyOfClientHandle();
                session = new(process, root, profile, new(fromBrowser, toBrowser), name);
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(20));
                await session._connection.SendAsync("Browser.getVersion", cancellationToken: startup.Token).ConfigureAwait(false);
                return session;
            }
            catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                lastError = error;
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                else
                {
                    toBrowser.Dispose(); fromBrowser.Dispose();
                    if (process is not null) { await StopProcessAsync(process).ConfigureAwait(false); process.Dispose(); }
                    await DeleteProfileAsync(root, profile).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        throw new InvalidOperationException("Edge or Chrome could not open a private sign-in connection. Update your browser, or check whether your organization has disabled browser debugging.", lastError);
    }

    internal async Task<JsonElement> CommandAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await _connection.SendAsync(method, parameters, sessionId, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The sign-in browser stopped responding. Close it and try again.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _connection.SendAsync("Browser.close", cancellationToken: close.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { }
            await StopProcessAsync(_process).ConfigureAwait(false);
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
            await DeleteProfileAsync(_profileRoot, _profile).ConfigureAwait(false);
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited) return;
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    internal static async Task DeleteProfileAsync(string root, string profile)
    {
        string resolved = Path.GetFullPath(profile);
        if (!string.Equals(Path.GetDirectoryName(resolved), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("signin-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(resolved)[7..], "N", out _)) throw new InvalidOperationException("Invalid owned sign-in profile.");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9) throw new BrowserProfileCleanupException(error);
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private static IEnumerable<(string Path, string Name)> InstalledBrowsers()
    {
        foreach (var (file, name, vendor) in new[] { ("msedge.exe", "Microsoft Edge", @"Microsoft\Edge\Application"), ("chrome.exe", "Google Chrome", @"Google\Chrome\Application") })
        {
            var candidates = new List<string>();
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + file);
                    if (key?.GetValue(null) is string path) candidates.Add(path.Trim().Trim('"'));
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
                candidates.Add(Path.Combine(Environment.GetFolderPath(folder), vendor, file));
            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) yield return (path, name);
        }
    }
}

internal sealed class BrowserProfileCleanupException(Exception innerException) : IOException("The temporary sign-in browser data could not be removed. Close its window and try again.", innerException);
