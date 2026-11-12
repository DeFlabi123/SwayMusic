using System.Reflection;

namespace SwayMusic.Desktop.Updates;

public enum UpdatePhase { Unavailable, Idle, Checking, Available, Downloading, Ready, Failed }

public sealed record UpdateStatus(UpdatePhase Phase, string Message, string? AvailableVersion = null,
    string Notes = "", int Progress = 0, bool CanDownload = false, bool CanInstall = false)
{
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
}

public interface IAppUpdateService : IDisposable
{
    string CurrentVersion { get; }
    bool IsSupported { get; }
    UpdateStatus Status { get; }
    event Action? Changed;
    Task InitializeAsync(CancellationToken token);
    Task CheckAsync(CancellationToken token);
    Task DownloadAsync(CancellationToken token);
    Task ValidatePendingAsync(CancellationToken token);
    void ApplyAfterExit(bool restart);
    void Cancel();
}

public static class AppVersion
{
    public static string Current => typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
}

internal sealed class UnavailableUpdateService : IAppUpdateService
{
    public string CurrentVersion => AppVersion.Current;
    public bool IsSupported => false;
    public UpdateStatus Status { get; } = new(UpdatePhase.Unavailable,
        "Install SwayMusic using its setup program to enable in-app updates.");
    public event Action? Changed { add { } remove { } }
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    public Task CheckAsync(CancellationToken token) => Task.CompletedTask;
    public Task DownloadAsync(CancellationToken token) => Task.CompletedTask;
    public Task ValidatePendingAsync(CancellationToken token) => throw new InvalidOperationException("No installed update is available.");
    public void ApplyAfterExit(bool restart) => throw new InvalidOperationException("No installed update is available.");
    public void Cancel() { }
    public void Dispose() { }
}
