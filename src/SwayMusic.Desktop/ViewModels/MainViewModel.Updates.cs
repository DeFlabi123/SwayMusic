using System.Windows.Input;
using Avalonia.Threading;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.Updates;

namespace SwayMusic.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IAppUpdateService _updates = null!;
    private readonly CancellationTokenSource _updatesLoopLifetime = new();
    private Task _updatesLoop = Task.CompletedTask;
    private bool _updatesStarted, _savedForUpdate;
    public event Action? UpdateRestartRequested;
    public ICommand CheckUpdatesCommand { get; private set; } = null!;
    public ICommand DownloadUpdateCommand { get; private set; } = null!;
    public ICommand CancelUpdateCommand { get; private set; } = null!;
    public ICommand RestartUpdateCommand { get; private set; } = null!;
    public string[] UpdateModeOptions { get; } = ["Automatic", "Notify only", "Manual"];
    public string UpdateMode
    {
        get => UpdateModeOptions.Contains(_library.State.Settings.UpdateMode) ? _library.State.Settings.UpdateMode : "Automatic";
        set
        {
            if (!UpdateModeOptions.Contains(value)) return;
            _library.State.Settings.UpdateMode = value;
            OnPropertyChanged();
            if (_initialized && !_disposed) TrackBackground(SaveUpdatePreferencesAsync());
        }
    }
    public string InstalledVersion => "Version " + _updates.CurrentVersion;
    public string UpdateMessage => _updates.Status.Message;
    public string AvailableUpdateVersion => _updates.Status.AvailableVersion is { } version ? "Available: " + version : "";
    public string UpdateReleaseNotes => _updates.Status.Notes;
    public string LastUpdateCheck => _library.State.Settings.LastUpdateCheckUtc is { } date
        ? "Last checked: " + date.ToLocalTime().ToString("g") : "Updates have not been checked yet.";
    public bool CanCheckUpdates => _updates.IsSupported && !_updates.Status.IsBusy && !_updates.Status.CanInstall;
    public bool CanDownloadUpdate => _updates.Status.CanDownload && !_updates.Status.IsBusy;
    public bool CanInstallUpdate => _updates.Status.CanInstall && !_updates.Status.IsBusy;
    public bool IsUpdateBusy => _updates.Status.IsBusy;
    public bool IsUpdateDownloading => _updates.Status.Phase == UpdatePhase.Downloading;
    public int UpdateDownloadProgress => _updates.Status.Progress;
    public bool HasUpdateNotes => !string.IsNullOrWhiteSpace(UpdateReleaseNotes);
    public bool HasUpdateAvailable => _updates.Status.AvailableVersion is not null;
    public bool ShouldApplyUpdateOnExit => UpdateMode == "Automatic" && CanInstallUpdate;

    private void InitializeUpdateCommands(IAppUpdateService updates)
    {
        _updates = updates;
        _updates.Changed += UpdatesChanged;
        CheckUpdatesCommand = Async(() => CheckUpdateAsync(automatic: false, _lifetime.Token));
        DownloadUpdateCommand = Async(async () => { if (CanDownloadUpdate) await _updates.DownloadAsync(_lifetime.Token); });
        CancelUpdateCommand = new RelayCommand(_updates.Cancel);
        RestartUpdateCommand = new RelayCommand(() => { if (IsReady && CanInstallUpdate) UpdateRestartRequested?.Invoke(); });
    }

    private void UpdatesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(UpdatesChanged); return; }
        if (_disposed) return;
        foreach (var name in new[] { nameof(InstalledVersion), nameof(UpdateMessage), nameof(AvailableUpdateVersion),
            nameof(UpdateReleaseNotes), nameof(LastUpdateCheck), nameof(CanCheckUpdates), nameof(CanDownloadUpdate),
            nameof(CanInstallUpdate), nameof(IsUpdateBusy), nameof(IsUpdateDownloading), nameof(UpdateDownloadProgress),
            nameof(HasUpdateNotes), nameof(HasUpdateAvailable), nameof(ShouldApplyUpdateOnExit) }) OnPropertyChanged(name);
    }

    private void StartUpdates()
    {
        OnPropertyChanged(nameof(UpdateMode));
        UpdatesChanged();
        if (_updatesStarted || !_updates.IsSupported) return;
        _updatesStarted = true;
        _updatesLoop = RunUpdateLoopAsync(_updatesLoopLifetime.Token);
    }

    private async Task RunUpdateLoopAsync(CancellationToken token)
    {
        try
        {
            await _updates.InitializeAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            while (!token.IsCancellationRequested)
            {
                var next = _library.State.Settings.NextUpdateCheckUtc;
                if (IsReady && UpdateMode != "Manual" && CanCheckUpdates
                    && (next is null || next <= DateTimeOffset.UtcNow || next > DateTimeOffset.UtcNow.AddDays(2)))
                    await CheckUpdateAsync(automatic: true, token);
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed) ReportError(error); }
    }

    private async Task CheckUpdateAsync(bool automatic, CancellationToken token)
    {
        if (!IsReady || !CanCheckUpdates || (automatic && UpdateMode == "Manual")) return;
        // Persist the backoff before contacting GitHub so repeated restarts cannot hammer its API.
        _library.State.Settings.NextUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(1);
        await SaveUpdatePreferencesAsync();
        await _updates.CheckAsync(token);
        if (_updates.Status.Phase != UpdatePhase.Failed)
        {
            _library.State.Settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _library.State.Settings.NextUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(1);
            await SaveUpdatePreferencesAsync();
        }
        UpdatesChanged();
        if (automatic && UpdateMode == "Automatic" && CanDownloadUpdate && !token.IsCancellationRequested)
            await _updates.DownloadAsync(token);
    }

    private async Task SaveUpdatePreferencesAsync()
    {
        await _library.SaveAsync(_lifetime.Token);
        OnPropertyChanged(nameof(LastUpdateCheck));
    }

    internal async Task PrepareUpdateExitAsync()
    {
        if (!IsReady || !CanInstallUpdate) throw new InvalidOperationException("A verified update is not ready.");
        await _updates.ValidatePendingAsync(_lifetime.Token);
        bool resume = Player.State == PlayerState.Playing;
        IsReady = false;
        Busy = true;
        Status = "Saving your library before updating…";
        Player.Pause();
        _pageRequest?.Cancel();
        _signInRequest?.Cancel();
        _sourcePrefetch.Cancel();
        try
        {
            var pending = _commands.Select(command => command.RunningTask).Concat(_backgroundTasks.ToArray()).ToArray();
            try { await Task.WhenAll(pending); }
            catch (Exception) { /* Individual operations already surface their own errors. */ }
            _library.State.Settings.Volume = Player.Volume;
            _library.State.Settings.Shuffle = Player.Shuffle;
            _library.State.Settings.RepeatMode = Player.Repeat.ToString();
            _library.State.Session = new() { Queue = Player.Queue.ToList(), CurrentIndex = Player.CurrentIndex, Position = Player.Position };
            await _library.SaveAsync(_lifetime.Token);
            _savedForUpdate = true;
        }
        catch
        {
            Busy = false;
            IsReady = true;
            if (resume) Player.Play();
            throw;
        }
    }

    internal void ApplyUpdateAfterExit(bool restart) => _updates.ApplyAfterExit(restart);
}
