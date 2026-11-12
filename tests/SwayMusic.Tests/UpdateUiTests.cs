using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using SwayMusic.Core;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.Updates;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

public static class UpdateUiTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var service = new FakeUpdates();
        var model = new MainViewModel(directory, appUpdates: service);
        var window = new MainWindow(model, integrateSystem: false);
        window.Show(); Flush(); Await(model.InitializeAsync());
        try
        {
            Check(service.Checks == 0, "Opening the window must not start a network update check.");
            model.SettingsCommand.Execute(null); Flush();
            var picker = window.FindControl<ComboBox>("UpdateModePicker")!;
            Check(Equals(picker.SelectedItem, "Automatic"), "Automatic updates must be the default.");
            Check(ReferenceEquals(window.FindControl<Button>("CheckUpdatesButton")!.Command, model.CheckUpdatesCommand),
                "The real update button must use the updater command.");
            picker.SelectedItem = "Notify only"; Flush();
            model.CheckUpdatesCommand.Execute(null); Await(((AsyncCommand)model.CheckUpdatesCommand).RunningTask);
            Check(service.Checks == 1 && service.Downloads == 0 && model.CanDownloadUpdate,
                "A manual check must expose an available update without downloading it automatically.");
            model.DownloadUpdateCommand.Execute(null); Await(((AsyncCommand)model.DownloadUpdateCommand).RunningTask); Flush();
            Check(model.CanInstallUpdate && !model.ShouldApplyUpdateOnExit && model.UpdateReleaseNotes == "Fixture notes",
                "Notify only must offer restart without installing on ordinary quit.");
            model.UpdateMode = "Automatic"; Flush();
            Check(model.ShouldApplyUpdateOnExit, "Automatic mode should install a ready update on normal exit.");
            model.UpdateMode = "Manual"; Flush();
            Check(!model.ShouldApplyUpdateOnExit, "Changing to Manual must immediately suppress automatic installation.");
            var scroll = window.FindControl<ScrollViewer>("SettingsScroll")!;
            scroll.Offset = new(0, scroll.Extent.Height); Flush();
            using (var frame = window.CaptureRenderedFrame())
                frame?.Save(Path.GetFullPath("artifacts/updates-settings.png"), PngBitmapEncoderOptions.Default);
            model.Player.RestoreQueue([new Track("update-queue", "Preserved song", "Fixture")], 0);
            // Ensure all preference saves complete before locking the destination.
            Await(model.SaveSettingsCommand is AsyncCommand save ? Run(save) : Task.CompletedTask);
            using (var locked = new FileStream(Path.Combine(directory, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false;
                try { Await(model.PrepareUpdateExitAsync()); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed && model.IsReady && !model.Busy && service.Applies == 0,
                    "A failed library save must keep the app usable and prevent updating.");
            }
            Await(model.PrepareUpdateExitAsync());
            Check(!model.IsReady && service.Validations == 2, "Only a successful save may prepare update shutdown.");
            using var stored = new LibraryStore(directory);
            Await(stored.LoadAsync());
            Check(stored.State.Session.Queue.Single().Id == "update-queue" && stored.State.Settings.UpdateMode == "Manual",
                "Queue and update preferences must survive the update handoff.");
            Await(model.DisposeAsync().AsTask());
            model.ApplyUpdateAfterExit(restart: true);
            Check(service.Applies == 1 && service.Restart, "Explicit restart must reach the apply handoff only after saving and disposal.");
        }
        finally { Await(model.DisposeAsync().AsTask()); window.Close(); Flush(); }
        var reopened = new MainViewModel(directory);
        Await(reopened.InitializeAsync());
        Check(reopened.UpdateMode == "Manual" && !reopened.CanCheckUpdates,
            "Isolated fixture builds must restore preferences while disabling production updates.");
        Await(reopened.DisposeAsync().AsTask());
        Console.WriteLine("PASS update UI: bindings, mode persistence, check/download controls, paused queue preservation, save-failure recovery and explicit restart handoff");
    }

    private static Task Run(AsyncCommand command) { command.Execute(null); return command.RunningTask; }

    private sealed class FakeUpdates : IAppUpdateService
    {
        public string CurrentVersion => "1.0.0";
        public bool IsSupported => true;
        public UpdateStatus Status { get; private set; } = new(UpdatePhase.Idle, "Fixture updater");
        public event Action? Changed;
        public int Checks, Downloads, Validations, Applies;
        public bool Restart;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task CheckAsync(CancellationToken token)
        {
            Checks++; Status = new(UpdatePhase.Available, "Fixture available", "1.1.0", "Fixture notes", CanDownload: true);
            Changed?.Invoke(); return Task.CompletedTask;
        }
        public Task DownloadAsync(CancellationToken token)
        {
            Downloads++; Status = new(UpdatePhase.Ready, "Fixture ready", "1.1.0", "Fixture notes", 100, CanInstall: true);
            Changed?.Invoke(); return Task.CompletedTask;
        }
        public Task ValidatePendingAsync(CancellationToken token) { Validations++; return Task.CompletedTask; }
        public void ApplyAfterExit(bool restart) { Applies++; Restart = restart; }
        public void Cancel() { }
        public void Dispose() { }
    }
}
