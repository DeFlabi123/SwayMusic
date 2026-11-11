using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using SwayMusic.Core;
using SwayMusic.Core.Storage;
using SwayMusic.Desktop;
using SwayMusic.Desktop.ViewModels;
using static SwayMusic.Tests.HeadlessTests;

namespace SwayMusic.Tests;

internal static class AccountLibraryUiTests
{
    private static readonly AccountSession Session = new("SAPISID=cloud-fixture", DisplayName: "Cloud fixture", ChannelId: "UC_account_a");
    public static void Run(string directory)
    {
        using (var local = new LibraryStore(directory))
        {
            Await(local.LoadAsync()); Await(local.ToggleLikeAsync(new Track("local-file", "Device song", "Fixture")));
            Await(local.CreatePlaylistAsync("Device playlist"));
        }
        Seed(directory, Session);
        using (var view = new View(directory))
        {
            Sync(view);
            Open(view.Model.LikesCommand);
            Check(view.Model.Tracks.Count == 2 && view.Model.Tracks.All(t => t.Track.Id != "local-file"), "signed-in liked songs come from the account");
            view.Model.SelectedTrack = view.Model.Tracks[0]; Open(view.Model.LikeCommand);
            Check(view.Model.Tracks.Count == 1 && view.Model.Status.Contains("YouTube Music", StringComparison.Ordinal), "unlike updates account and confirmed local snapshot");
            Open(view.Model.PlaylistsCommand);
            Check(view.Model.BrowseItems.Count == 3 && view.Model.Playlists.Count(p => p.IsRemote) == 2, "cloud and preserved device playlists are available");
            Check(view.Model.PlaylistTargets.Count() == 2, "only owned remote playlists and device playlists are add targets");
            view.Model.SelectedBrowseItem = view.Model.BrowseItems.First(p => p.CanEdit);
            view.Model.NewPlaylistName = "Renamed cloud"; Open(view.Model.RenamePlaylistCommand);
            Check(view.Model.BrowseItems.Any(p => p.Title == "Renamed cloud"), "remote rename updates playlist list");
            view.Model.NewPlaylistName = "Created cloud"; Open(view.Model.CreatePlaylistCommand);
            Check(view.Model.Playlists.Any(p => p.IsRemote && p.Name == "Created cloud"), "signed-in create is a remote playlist");
            Open(view.Model.ArtistsCommand);
            Check(view.Model.IsArtists && view.Model.ShowBrowse && view.Model.BrowseItems.Single().Title == "Cloud artist", "Artists navigation shows subscribed artists");
            using (var frame = view.Window.CaptureRenderedFrame())
            {
                var preview = Path.GetFullPath("docs/sway-artists.png");
                try { frame?.Save(preview, PngBitmapEncoderOptions.Default); }
                catch (IOException) when (File.Exists(preview)) { Console.WriteLine("Artist preview is open; keeping the previous optional capture."); }
            }
            Open(view.Model.ToggleArtistSubscriptionCommand);
            Check(view.Model.BrowseItems.Count == 0 && view.Http.Requests.Any(r => r.Path == "subscription/unsubscribe"), "unsubscribe updates the account and artist list");
            view.Http.FailRead = true; Sync(view); Open(view.Model.LikesCommand);
            Check(view.Model.Tracks.Count == 1 && view.Model.SyncStatus.Contains("kept", StringComparison.Ordinal), "a failed refresh preserves the confirmed snapshot");
        }
        using (var offline = new View(directory, new() { FailRead = true }))
        {
            Sync(offline); Open(offline.Model.LikesCommand);
            Check(offline.Model.Tracks.Count == 1, "restart restores account cache when network sync fails");
            Open(offline.Model.SignOutCommand); Open(offline.Model.ArtistsCommand);
            Check(!offline.Model.IsSignedIn && offline.Model.BrowseItems.Count == 0, "sign-out clears account collections");
            Open(offline.Model.PlaylistsCommand);
            Check(offline.Model.BrowseItems.Count == 1 && offline.Model.BrowseItems[0].Kind == "local", "sign-out preserves device playlists");
        }
        Seed(directory, Session with { CookieHeader = "SAPISID=other-account", ChannelId = "UC_account_b" });
        using (var other = new View(directory, new() { FailRead = true }))
        {
            Sync(other); Open(other.Model.LikesCommand);
            Check(other.Model.Tracks.Count == 0, "a different account cannot load the previous account cache");
        }
        Seed(directory, Session);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AccountLibraryTests.LibraryHttp { Hold = held.Task };
        using (var race = new View(directory, handler))
        {
            Await(handler.Started.Task);
            race.Model.LibraryCommand.Execute(null); Flush();
            race.Model.SelectedTrack = race.Model.Tracks.First(t => t.Track.Id == "bcdefghijkl");
            race.Model.LikeCommand.Execute(null);
            var like = ((AsyncCommand)race.Model.LikeCommand).RunningTask;
            race.Model.SyncLibraryCommand.Execute(null);
            var sync = ((AsyncCommand)race.Model.SyncLibraryCommand).RunningTask;
            Open(race.Model.SignOutCommand);
            held.SetResult(); Await(sync); Open(race.Model.ArtistsCommand);
            try { Await(like); } catch (OperationCanceledException) { }
            Check(!race.Model.IsSignedIn && race.Model.BrowseItems.Count == 0, "late sync results cannot restore an account after sign-out");
            Check(handler.Requests.All(r => r.Path == "browse") && race.Model.Status == "Signed out.", "a like waiting for sync cannot mutate a later account or overwrite sign-out status");
        }
        using var preserved = new LibraryStore(directory); Await(preserved.LoadAsync());
        Check(preserved.State.Playlists.Count == 1 && preserved.State.LikedTracks.Single().Id == "local-file", "account sync never overwrites device library state");
        Console.WriteLine("PASS account collection UI, remote edits, cached/offline data, device preservation, account isolation and sign-out race");
    }
    private static void Seed(string directory, AccountSession session) { using var vault = new CredentialStore(directory); Await(vault.SaveAccountAsync(session)); }
    private static void Open(System.Windows.Input.ICommand command) { command.Execute(null); if (command is AsyncCommand asyncCommand) Await(asyncCommand.RunningTask); Flush(); }
    private static void Sync(View view) => Open(view.Model.SyncLibraryCommand);
    private sealed class View : IDisposable
    {
        public AccountLibraryTests.LibraryHttp Http { get; }
        public MainViewModel Model { get; }
        public MainWindow Window { get; }
        public View(string directory, AccountLibraryTests.LibraryHttp? http = null)
        {
            Http = http ?? new(); Model = new(directory, httpHandler: Http, autoSyncAccount: true);
            Window = new(Model, integrateSystem: false); Window.Show(); Await(Model.InitializeAsync()); Flush();
        }
        public void Dispose() { Await(Model.DisposeAsync().AsTask()); Window.Close(); Flush(); }
    }
}
