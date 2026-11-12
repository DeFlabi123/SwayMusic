using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SwayMusic.Desktop.Playback;
using SwayMusic.Desktop.ViewModels;

namespace SwayMusic.Desktop;

public partial class OrbitView : UserControl
{
    private MainViewModel _model = null!;
    private readonly DispatcherTimer _animation;
    private readonly DispatcherTimer _idle;
    private readonly ScaleTransform _coverPulse = new(1, 1);
    private bool _active, _hostVisible = true, _keyboardInteraction;
    public event EventHandler? ExitRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? NextDisplayRequested;

    public OrbitView()
    {
        InitializeComponent();
        OrbitAlbumDisc.RenderTransform = _coverPulse;
        _animation = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
        {
            if (!_active) return;
            Halo.Update(_model.Player);
            _coverPulse.ScaleX = _coverPulse.ScaleY = 1 + Halo.Energy * 0.025;
        });
        _animation.Stop();
        _idle = new() { Interval = TimeSpan.FromSeconds(4) };
        _idle.Tick += (_, _) => HideControlsIfIdle();
        TopChrome.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) } };
        BottomChrome.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(350) } };
        OrbitAmbientProgress.Transitions = new Transitions { new DoubleTransition { Property = RangeBase.ValueProperty, Duration = TimeSpan.FromMilliseconds(240) } };
        SizeChanged += (_, _) => { if (_active) ResizeScene(); };
        AddHandler(PointerMovedEvent, (_, _) => Wake(), RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => Wake(), RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        OrbitSeekSlider.AddHandler(PointerPressedEvent, (_, _) => { if (_active) _model.BeginSeek(); }, RoutingStrategies.Tunnel, handledEventsToo: true);
        OrbitSeekSlider.AddHandler(PointerReleasedEvent, SeekReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    internal void Activate(MainViewModel model)
    {
        if (_active) { Focus(); Wake(); return; }
        _model = model; DataContext = model; _active = true;
        model.PropertyChanged += ModelChanged;
        var host = TopLevel.GetTopLevel(this) as Window;
        MoveOrbitDisplayButton.IsVisible = host?.Screens.All.Count > 1;
        _hostVisible = host is not null && host.IsVisible && host.WindowState != WindowState.Minimized;
        ResizeScene(); UpdatePalette(); UpdateActivity(); Wake(); Focus();
    }
    internal void Deactivate()
    {
        if (!_active) return;
        _active = false; _animation.Stop(); _idle.Stop();
        _model.PropertyChanged -= ModelChanged; _model.SetOrbitVisible(false);
        OrbitQueuePanel.IsVisible = false; ResetHalo(); ShowChrome(true);
    }
    internal void UpdateHostVisibility(bool visible)
    { _hostVisible = visible; if (_active) { UpdateActivity(); if (visible) Wake(); } }
    private void ResetHalo() { Halo.Reset(); _coverPulse.ScaleX = _coverPulse.ScaleY = 1; }
    private void UpdateActivity()
    {
        bool visible = _active && _hostVisible;
        _model.SetOrbitVisible(visible);
        if (visible && !_model.OrbitCalm && _model.Player.State == PlayerState.Playing) _animation.Start();
        else { _animation.Stop(); _idle.Stop(); ResetHalo(); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_active) return;
        if (args.PropertyName is nameof(MainViewModel.OrbitPrimaryBrush) or nameof(MainViewModel.OrbitSecondaryBrush)) UpdatePalette();
        if (args.PropertyName == nameof(MainViewModel.OrbitShowLyrics)) ResizeScene();
        if (args.PropertyName is nameof(MainViewModel.PlayIconKind) or nameof(MainViewModel.OrbitCalm))
        {
            bool animate = _hostVisible && !_model.OrbitCalm && _model.Player.State == PlayerState.Playing;
            if (animate) _animation.Start(); else { _animation.Stop(); ResetHalo(); }
            if (_model.Player.State == PlayerState.Playing && _hostVisible && !_idle.IsEnabled) _idle.Start();
            if (_model.Player.State != PlayerState.Playing) { _idle.Stop(); ShowChrome(true); }
        }
    }
    private void UpdatePalette()
    { Halo.Primary = _model.OrbitPrimaryBrush; Halo.Secondary = _model.OrbitSecondaryBrush; Halo.InvalidateVisual(); }
    private void ResizeScene()
    {
        var host = TopLevel.GetTopLevel(this);
        double width = Bounds.Width > 0 ? Bounds.Width : host?.ClientSize.Width ?? 1280;
        double height = Bounds.Height > 0 ? Bounds.Height : host?.ClientSize.Height ?? 800;
        Grid.SetColumnSpan(DiscColumn, _model.OrbitShowLyrics ? 1 : 2);
        double disc = Math.Clamp(Math.Min(width * (_model.OrbitShowLyrics ? 0.35 : 0.7), (height - 330) * 0.85), 180, 520);
        OrbitDisc.Width = disc; OrbitDisc.Height = disc;
        DiscColumn.Width = Math.Max(disc, Math.Min(340, width * 0.36));
        OrbitMetadata.Margin = new Thickness(disc * 0.1, 8, 0, 0);
        double font = Math.Clamp(height * 0.041, 24, 40);
        foreach (var line in new[] { LyricBefore2, LyricBefore, LyricAfter, LyricAfter2 }) line.FontSize = font;
        LyricNow.FontSize = font * 1.22;
        PlainLyricsScroll.MaxHeight = Math.Max(100, height - 280);
        OrbitLayout.Margin = new Thickness(width < 1000 ? 28 : 48, 24, width < 1000 ? 28 : 48, 26);
        OrbitAmbientProgressHost.Width = Math.Clamp(width * 0.58, 240, 840);
        OrbitScene.ColumnSpacing = width < 1000 ? 24 : 44;
    }
    internal void HideControlsIfIdle()
    {
        _idle.Stop();
        if (_active && _model.Player.State == PlayerState.Playing && !TopChrome.IsPointerOver && !BottomChrome.IsPointerOver && !OrbitQueuePanel.IsVisible && !(_keyboardInteraction && (TopChrome.IsKeyboardFocusWithin || BottomChrome.IsKeyboardFocusWithin))) ShowChrome(false);
    }
    private void ShowChrome(bool show)
    { if (!show && (TopChrome.IsKeyboardFocusWithin || BottomChrome.IsKeyboardFocusWithin)) Focus(); TopChrome.Opacity = BottomChrome.Opacity = show ? 1 : 0; TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = show; }
    private void Wake(bool keyboard = false)
    {
        if (!_active) return;
        _keyboardInteraction = keyboard;
        ShowChrome(true); _idle.Stop();
        if (_model.Player.State == PlayerState.Playing && _hostVisible) _idle.Start();
    }
    private void ExitClicked(object? sender, RoutedEventArgs args) => ExitRequested?.Invoke(this, EventArgs.Empty);
    private void MoveDisplayClicked(object? sender, RoutedEventArgs args) => NextDisplayRequested?.Invoke(this, EventArgs.Empty);
    private void QueueClicked(object? sender, RoutedEventArgs args) { OrbitQueuePanel.IsVisible = !OrbitQueuePanel.IsVisible; Wake(); }
    private async void QueueTapped(object? sender, TappedEventArgs args)
    {
        if (args.Source is not Control source) return;
        var row = (source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault())?.DataContext as TrackRow;
        if (row is null) return;
        _model.SelectedQueueTrack = row; OrbitQueuePanel.IsVisible = false;
        try { await _model.PlayQueueSelectedAsync(); } catch (OperationCanceledException) { } catch (Exception error) { _model.ReportError(error); }
    }
    private async void SeekReleased(object? sender, PointerReleasedEventArgs args) { if (_active) await _model.EndSeekAsync(TimeSpan.FromSeconds(OrbitSeekSlider.Value)); }
    private async void SeekKeyUp(object? sender, KeyEventArgs args)
    { if (_active && args.Key is Key.Left or Key.Right or Key.Home or Key.End) { args.Handled = true; await _model.SeekSafelyAsync(TimeSpan.FromSeconds(OrbitSeekSlider.Value)); } }
    private async void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (!_active) return;
        Wake(keyboard: true);
        if (args.Key == Key.Escape) { args.Handled = true; if (OrbitQueuePanel.IsVisible) OrbitQueuePanel.IsVisible = false; else ExitRequested?.Invoke(this, EventArgs.Empty); return; }
        if (args.Key == Key.F11) { args.Handled = true; FullscreenRequested?.Invoke(this, EventArgs.Empty); return; }
        if (args.Source is Control source && (source is Button or Slider or ToggleButton || source.GetVisualAncestors().Any(parent => parent is Button or Slider or ToggleButton))) return;
        if (args.Key == Key.Space) { args.Handled = true; _model.ToggleCommand.Execute(null); }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Right) { args.Handled = true; _model.NextCommand.Execute(null); }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.Left) { args.Handled = true; _model.PreviousCommand.Execute(null); }
        else if (args.Key is Key.Left or Key.Right) { args.Handled = true; await _model.SeekSafelyAsync(_model.Player.Position + TimeSpan.FromSeconds(args.Key == Key.Right ? 5 : -5)); }
    }
}
