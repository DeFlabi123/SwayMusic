using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.ViewModels;

namespace SwayMusic.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _integrateSystem;
    private WindowsMediaControls? _mediaControls;
    private bool _closing, _closed;

    public MainWindow() : this(new MainViewModel()) { }

    public MainWindow(MainViewModel viewModel, bool integrateSystem = true)
    {
        _viewModel = viewModel;
        _integrateSystem = integrateSystem;
        InitializeComponent();
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty || args.Property == IsVisibleProperty)
                _viewModel.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        };
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);
        SeekSlider.AddHandler(PointerPressedEvent, SeekPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, SeekReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => _viewModel.SetAvailableWidth(Bounds.Width);
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        await _viewModel.InitializeAsync();
        if (_closing || _closed) return;
        var size = _viewModel.GetWindowSize();
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        Width = Math.Clamp(size.Width, MinWidth, Math.Max(MinWidth, (screen?.WorkingArea.Width ?? 1920) / (screen?.Scaling ?? 1)));
        Height = Math.Clamp(size.Height, MinHeight, Math.Max(MinHeight, (screen?.WorkingArea.Height ?? 1080) / (screen?.Scaling ?? 1)));
        _viewModel.SetAvailableWidth(Width);
        try
        {
            if (_integrateSystem && TryGetPlatformHandle() is { } handle)
            {
                _mediaControls = new(_viewModel.Player, handle.Handle, action => Dispatcher.UIThread.Post(action));
                _mediaControls.Error += (_, error) => _viewModel.ReportError(error);
            }
        }
        catch (Exception ex) { _viewModel.ReportError(new InvalidOperationException("Windows media controls are unavailable: " + ex.Message)); }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        if (WindowState == WindowState.Normal) _viewModel.SetWindowSize(Width, Height);
        try
        {
            try { _mediaControls?.Dispose(); }
            finally { await _viewModel.DisposeAsync(); }
        }
        catch (Exception ex) { _viewModel.ReportError(ex); }
        finally { _closed = true; Close(); }
    }

    private void SearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) { _viewModel.SearchCommand.Execute(null); args.Handled = true; }
    }
    private void TrackDoubleTapped(object? sender, TappedEventArgs args) => _viewModel.PlaySelectedCommand.Execute(null);
    private void FocusSearchClicked(object? sender, RoutedEventArgs args) { SearchBox.Focus(); SearchBox.SelectAll(); }
    private async void HomeCardClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: HomeCard card }) return;
        try { await _viewModel.PlayHomeCardAsync(card); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _viewModel.ReportError(error); }
    }
    private async void QueueDoubleTapped(object? sender, TappedEventArgs args)
    {
        try { await _viewModel.PlayQueueSelectedAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _viewModel.ReportError(error); }
    }
    private void TrackContextRequested(object? sender, ContextRequestedEventArgs args)
    {
        if (args.Source is Control { DataContext: TrackRow row }) _viewModel.SelectedTrack = row;
    }
    private void RowActionsClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: TrackRow row } button) return;
        _viewModel.SelectedTrack = row;
        TrackList.ContextMenu?.Open(button);
    }
    private void TrackMenuClicked(object? sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { Tag: string action }) return;
        var command = action switch
        {
            "play" => _viewModel.PlaySelectedCommand, "enqueue" => _viewModel.EnqueueCommand,
            "like" => _viewModel.LikeCommand, "radio" => _viewModel.RadioCommand,
            "download" => _viewModel.DownloadCommand, "up" => _viewModel.MoveUpCommand,
            "down" => _viewModel.MoveDownCommand, _ => _viewModel.RemoveCommand
        };
        command.Execute(null);
    }
    private async void AddToPlaylistClicked(object? sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedTrack is null) return;
        var selectedSongId = _viewModel.SelectedTrack.Track.Id;
        var accountGeneration = _viewModel.AccountOperationGeneration;
        var picker = new ComboBox { ItemsSource = _viewModel.PlaylistTargets.ToArray(), SelectedItem = _viewModel.SelectedPlaylist, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        picker.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SwayMusic.Core.Storage.LocalPlaylist>((playlist, _) => new TextBlock { Text = playlist is null ? "" : playlist.Name + (playlist.IsRemote ? " · YouTube Music" : " · On this device") });
        var dialog = new Window { Title = "Add to playlist", Width = 360, Height = 200, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var add = new Button { Content = "Add song" }; var cancel = new Button { Content = "Cancel" };
        add.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 18, Children = { new TextBlock { Text = "Choose a playlist", FontWeight = Avalonia.Media.FontWeight.SemiBold }, picker, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { add, cancel } } } };
        if (await dialog.ShowDialog<bool>(this) && picker.SelectedItem is SwayMusic.Core.Storage.LocalPlaylist playlist)
        {
            if (_viewModel.AccountOperationGeneration != accountGeneration || _viewModel.SelectedTrack?.Track.Id != selectedSongId)
            { _viewModel.ReportError(new InvalidOperationException("The account or selected song changed. Select the song and playlist again.")); return; }
            _viewModel.SelectedPlaylist = playlist; _viewModel.AddToPlaylistCommand.Execute(null);
        }
    }
    private async void BrowseDoubleTapped(object? sender, TappedEventArgs args)
    {
        try { await _viewModel.OpenSelectedBrowseAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }
    private void SeekPressed(object? sender, PointerPressedEventArgs args) => _viewModel.BeginSeek();
    private async void SeekReleased(object? sender, PointerReleasedEventArgs args) => await _viewModel.EndSeekAsync(TimeSpan.FromSeconds(SeekSlider.Value));
    private async void SeekKeyUp(object? sender, KeyEventArgs args)
    {
        if (args.Key is Key.Left or Key.Right or Key.Home or Key.End) { args.Handled = true; await _viewModel.SeekSafelyAsync(TimeSpan.FromSeconds(SeekSlider.Value)); }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        if (!_viewModel.IsReady) return;
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); args.Handled = true; return; }
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.O) { await PickFilesAsync(); args.Handled = true; return; }
        if (args.Source is Control source && (source is TextBox or NumericUpDown or Button or ToggleButton or MenuItem or ComboBox or Slider || source.GetVisualAncestors().Any(parent => parent is TextBox or NumericUpDown or Button or ToggleButton or MenuItem or ComboBox or Slider))) return;
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Right) { _viewModel.NextCommand.Execute(null); args.Handled = true; }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Left) { _viewModel.PreviousCommand.Execute(null); args.Handled = true; }
        else if (args.Key == Key.Space) { _viewModel.ToggleCommand.Execute(null); args.Handled = true; }
        else if (args.Key is Key.Left or Key.Right) { await _viewModel.SeekSafelyAsync(_viewModel.Player.Position + TimeSpan.FromSeconds(args.Key == Key.Right ? 5 : -5)); args.Handled = true; }
    }

    private async void OpenFilesClicked(object? sender, RoutedEventArgs args) => await PickFilesAsync();
    private async Task PickFilesAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Open audio files", AllowMultiple = true,
                FileTypeFilter = [new("Audio") { Patterns = ["*.mp3", "*.m4a", "*.aac", "*.wav", "*.flac", "*.wma"] }, FilePickerFileTypes.All]
            });
            await _viewModel.OpenLocalFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }
    private void OpenBrowserClicked(object? sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("https://music.youtube.com") { UseShellExecute = true }); }
        catch (Exception ex) { _viewModel.ReportError(ex); }
    }

    private async void DeletePlaylistClicked(object? sender, RoutedEventArgs args)
    {
        if (_viewModel.SelectedBrowseItem is not { } playlist || playlist.Kind is not ("local" or "remote-playlist")) return;
        var accountGeneration = _viewModel.AccountOperationGeneration;
        var dialog = new Window
        {
            Title = playlist.Kind == "local" ? "Delete local playlist" : playlist.CanEdit ? "Delete YouTube Music playlist" : "Remove saved playlist", Width = 400, Height = 185,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var delete = new Button { Content = playlist.Kind == "remote-playlist" && !playlist.CanEdit ? "Remove from library" : "Delete playlist" };
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 18,
            Children =
            {
                new TextBlock { Text = playlist.Kind == "local" ? $"Delete ‘{playlist.Title}’? Audio files will be retained." : playlist.CanEdit ? $"Delete ‘{playlist.Title}’ from your YouTube Music account?" : $"Remove ‘{playlist.Title}’ from your YouTube Music library?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { cancel, delete } }
            }
        };
        if (await dialog.ShowDialog<bool>(this))
        {
            if (_viewModel.AccountOperationGeneration != accountGeneration)
            { _viewModel.ReportError(new InvalidOperationException("The account changed. Select the playlist again.")); return; }
            _viewModel.SelectedBrowseItem = playlist;
            _viewModel.DeletePlaylistCommand.Execute(null);
        }
    }
}
