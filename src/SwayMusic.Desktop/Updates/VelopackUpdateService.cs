using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SwayMusic.Core.Storage;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;

namespace SwayMusic.Desktop.Updates;

internal sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly SignedGithubSource _source;
    private readonly UpdateManager _manager;
    private readonly IVelopackLocator _locator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _request;
    private UpdateInfo? _available;
    private UpdateProof? _pendingProof;
    private SignedUpdateRelease? _pending;
    private bool _disposed;
    private string ProofPath => Path.Combine(_locator.PackagesDir!, "sway-pending-update.json");

    public static IAppUpdateService Create()
    {
        var locator = VelopackLocator.Current;
        return locator.CurrentlyInstalledVersion is not null && !locator.IsPortable
            && locator.AppId == SignedUpdateRelease.AppId && locator.Channel == SignedUpdateRelease.StableChannel
            ? new VelopackUpdateService(locator) : new UnavailableUpdateService();
    }

    internal VelopackUpdateService(IVelopackLocator locator, HttpMessageHandler? handler = null, string? publicKey = null)
    {
        _locator = locator;
        using var stream = typeof(VelopackUpdateService).Assembly.GetManifestResourceStream(
            "SwayMusic.Desktop.Updates.update-public-key.pem")!;
        using var reader = new StreamReader(stream);
        _publicKey = publicKey ?? reader.ReadToEnd();
        _http = new(handler ?? new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
            { Timeout = TimeSpan.FromMinutes(20) };
        _source = new(_http, _publicKey);
        _manager = new(_source, new UpdateOptions { ExplicitChannel = SignedUpdateRelease.StableChannel, AllowVersionDowngrade = false }, locator);
    }

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Current;
    public bool IsSupported => _manager.IsInstalled;
    public UpdateStatus Status { get; private set; } = new(UpdatePhase.Idle, "Updates are checked in the background. Playback is never interrupted.");
    public event Action? Changed;
    private void Publish(UpdateStatus status) { Status = status; Changed?.Invoke(); }

    public Task InitializeAsync(CancellationToken token) => RunAsync(async ct =>
    {
        if (!File.Exists(ProofPath)) return;
        if (new FileInfo(ProofPath).Length > 256 * 1024) throw new InvalidDataException("Pending update metadata is too large.");
        var proof = await AtomicFile.ReadJsonAsync<UpdateProof>(ProofPath, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Pending update metadata is empty.");
        var release = proof.Verify(_publicKey);
        if (SemanticVersion.Parse(release.Version) <= _manager.CurrentVersion) { File.Delete(ProofPath); return; }
        await release.VerifyPackageAsync(Path.Combine(_locator.PackagesDir!, release.FileName), ct).ConfigureAwait(false);
        _pending = release; _pendingProof = proof;
        Publish(Ready(release));
    }, token);

    public Task CheckAsync(CancellationToken token) => RunAsync(async ct =>
    {
        if (_pending is not null) { Publish(Ready(_pending)); return; }
        Publish(new(UpdatePhase.Checking, "Checking GitHub for updates…"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        _source.CheckToken = timeout.Token;
        _available = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        Publish(_available is null ? new(UpdatePhase.Idle, "You have the latest published version.")
            : new(UpdatePhase.Available, "An update is available.", _available.TargetFullRelease.Version.ToString(),
                _available.TargetFullRelease.NotesMarkdown ?? "", CanDownload: true));
    }, token);

    public Task DownloadAsync(CancellationToken token) => RunAsync(async ct =>
    {
        var update = _available ?? throw new InvalidOperationException("Check for updates before downloading.");
        var proof = _source.Proof ?? throw new InvalidDataException("The update signature is missing.");
        var release = proof.Verify(_publicKey);
        Publish(new(UpdatePhase.Downloading, "Downloading update…", release.Version, release.Notes));
        string path = Path.Combine(_locator.PackagesDir!, release.FileName);
        // The SDK skips verification of an already-cached full package; authenticate it ourselves first.
        if (File.Exists(path))
        {
            try { await release.VerifyPackageAsync(path, ct).ConfigureAwait(false); }
            catch (CryptographicException) { File.Delete(path); }
        }
        await _manager.DownloadUpdatesAsync(update, p => Publish(new(UpdatePhase.Downloading,
            "Downloading update…", release.Version, release.Notes, p)), ct).ConfigureAwait(false);
        await release.VerifyPackageAsync(path, ct).ConfigureAwait(false);
        await AtomicFile.WriteJsonAsync(ProofPath, proof, ct).ConfigureAwait(false);
        _pending = release; _pendingProof = proof;
        Publish(Ready(release));
    }, token);

    public async Task ValidatePendingAsync(CancellationToken token)
    {
        var release = _pendingProof?.Verify(_publicKey) ?? throw new InvalidOperationException("Download a verified update first.");
        if (SemanticVersion.Parse(release.Version) <= _manager.CurrentVersion)
            throw new InvalidDataException("This update is not newer than the installed application.");
        try { await release.VerifyPackageAsync(Path.Combine(_locator.PackagesDir!, release.FileName), token).ConfigureAwait(false); }
        catch
        {
            _pending = null; _pendingProof = null;
            Publish(new(UpdatePhase.Failed, "The pending update failed verification. Check and download it again."));
            throw;
        }
    }

    public void ApplyAfterExit(bool restart)
    {
        ValidatePendingAsync(CancellationToken.None).GetAwaiter().GetResult();
        _manager.WaitExitThenApplyUpdates(_pending!.ToAsset(), silent: !restart, restart: restart);
    }

    private static UpdateStatus Ready(SignedUpdateRelease release) => new(UpdatePhase.Ready,
        "Update downloaded. Restart when convenient, or let Automatic mode install it when you close SwayMusic.",
        release.Version, release.Notes, 100, CanInstall: true);

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        if (_disposed || !await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        _request = request;
        try { await action(request.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Publish(new(UpdatePhase.Failed, "Update operation cancelled or timed out.", CanDownload: _available is not null)); }
        catch (Exception error) when (error is IOException or HttpRequestException or CryptographicException
            or JsonException or FormatException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
            or KeyNotFoundException or AcquireLockFailedException or ChecksumFailedException or NotInstalledException)
        { Publish(new(UpdatePhase.Failed, "Could not update SwayMusic: " + error.Message, CanDownload: _available is not null)); }
        finally { _request = null; _gate.Release(); }
    }

    public void Cancel() { try { _request?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); _http.Dispose(); }
}
