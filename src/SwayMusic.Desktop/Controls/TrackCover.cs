using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using SwayMusic.Core;
using SwayMusic.Desktop.ViewModels;

namespace SwayMusic.Desktop.Controls;

// List containers are recycled. Each visible cover owns its small decoded image and
// cancels obsolete requests whenever it is detached or rebound to a different song.
public sealed class TrackCover : Image
{
    public static readonly StyledProperty<Track?> TrackProperty = AvaloniaProperty.Register<TrackCover, Track?>(nameof(Track));
    public static readonly StyledProperty<BrowseItem?> ItemProperty = AvaloniaProperty.Register<TrackCover, BrowseItem?>(nameof(Item));
    public static readonly StyledProperty<MainViewModel?> OwnerProperty = AvaloniaProperty.Register<TrackCover, MainViewModel?>(nameof(Owner));
    private CancellationTokenSource? _request;
    private Bitmap? _bitmap;
    private bool _attached, _inViewport;
    public Track? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public BrowseItem? Item { get => GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public MainViewModel? Owner { get => GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }
    public TrackCover()
    {
        EffectiveViewportChanged += (_, args) =>
        {
            bool visible = IsEffectivelyVisible && args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            if (_inViewport == visible) return;
            _inViewport = visible; Reload();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; Reload(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; _inViewport = false; Clear(); base.OnDetachedFromVisualTree(e); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackProperty || change.Property == ItemProperty || change.Property == OwnerProperty) Reload();
    }

    private void Clear()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        Source = null; _bitmap?.Dispose(); _bitmap = null;
    }

    private void Reload()
    {
        Clear();
        if (!_attached || !_inViewport || !IsEffectivelyVisible || Owner is not { } owner) return;
        var track = Track ?? (Item is { } item ? new Track(item.Id, item.Title, item.Subtitle, ThumbnailUrl: item.ThumbnailUrl) : null);
        if (track is null) return;
        _request = new();
        _ = LoadAsync(owner, track, _request.Token);
    }

    private async Task LoadAsync(MainViewModel owner, Track track, CancellationToken token)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await owner.LoadTrackCoverAsync(track, token);
            if (token.IsCancellationRequested || !_attached) return;
            _bitmap = bitmap; Source = bitmap; bitmap = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The owning view model's task observer reports unexpected failures. */ }
        finally { bitmap?.Dispose(); }
    }
}
