using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Metrolist.Desktop.Playback;
using Metrolist.Desktop.ViewModels;

namespace Metrolist.Desktop;

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
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        await _viewModel.InitializeAsync();
        if (_closing || _closed) return;
        var size = _viewModel.GetWindowSize();
        Width = Math.Clamp(size.Width, MinWidth, Screens.Primary?.WorkingArea.Width ?? 1920);
        Height = Math.Clamp(size.Height, MinHeight, Screens.Primary?.WorkingArea.Height ?? 1080);
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
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); args.Handled = true; return; }
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.O) { await PickFilesAsync(); args.Handled = true; return; }
        if (args.Source is TextBox or NumericUpDown || args.Source == SeekSlider) return;
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
        if (_viewModel.SelectedBrowseItem is not { Kind: "local" } playlist) return;
        var dialog = new Window
        {
            Title = "Delete local playlist", Width = 400, Height = 185,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var delete = new Button { Content = "Delete playlist" };
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 18,
            Children =
            {
                new TextBlock { Text = $"Delete ‘{playlist.Title}’? Audio files will be retained.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { cancel, delete } }
            }
        };
        if (await dialog.ShowDialog<bool>(this)) _viewModel.DeletePlaylistCommand.Execute(null);
    }
}
