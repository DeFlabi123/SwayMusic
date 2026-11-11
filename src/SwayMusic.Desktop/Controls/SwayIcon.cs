using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace SwayMusic.Desktop.Controls;

public sealed class SwayIcon : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<SwayIcon, string>(nameof(Kind), "music");
    private static readonly Dictionary<string, Geometry> Shapes = new(StringComparer.Ordinal)
    {
        ["home"] = Geometry.Parse("M3,10 L12,3 L21,10 M5,9 V21 H19 V9 M10,21 V14 H14 V21"),
        ["search"] = Geometry.Parse("M18,10 A7,7 0 1 1 4,10 A7,7 0 1 1 18,10 M16,16 L22,22"),
        ["library"] = Geometry.Parse("M4,4 V21 M9,4 V21 M14,4 V21 M18,4 L22,21"),
        ["heart"] = Geometry.Parse("M12,21 L3,12 C-2,4 7,-1 12,6 C17,-1 26,4 21,12 Z"),
        ["download"] = Geometry.Parse("M12,2 V16 M6,10 L12,16 L18,10 M3,17 V22 H21 V17"),
        ["history"] = Geometry.Parse("M4,7 A9,9 0 1 1 3,15 M3,2 V8 H9 M12,6 V12 L16,15"),
        ["playlist"] = Geometry.Parse("M3,5 H17 M3,11 H17 M3,17 H12 M18,14 V22 M14,18 H22"),
        ["queue"] = Geometry.Parse("M3,5 H21 M3,11 H21 M3,17 H12 M17,15 L22,18 L17,21 Z"),
        ["lyrics"] = Geometry.Parse("M5,3 H19 V21 H5 Z M8,8 H16 M8,12 H16 M8,16 H13"),
        ["settings"] = Geometry.Parse("M3,6 H21 M3,18 H21 M8,3 V9 M16,15 V21"),
        ["music"] = Geometry.Parse("M10,18 V5 L21,3 V16 M10,5 L21,3 M10,18 A3,2 0 1 1 4,18 A3,2 0 1 1 10,18 M21,16 A3,2 0 1 1 15,16 A3,2 0 1 1 21,16"),
        ["more"] = Geometry.Parse("M5,12 A1,1 0 1 1 3,12 A1,1 0 1 1 5,12 M13,12 A1,1 0 1 1 11,12 A1,1 0 1 1 13,12 M21,12 A1,1 0 1 1 19,12 A1,1 0 1 1 21,12"),
        ["play"] = Geometry.Parse("M7,3 L21,12 L7,21 Z"),
        ["pause"] = Geometry.Parse("M8,4 V20 M16,4 V20"),
        ["next"] = Geometry.Parse("M4,4 L16,12 L4,20 Z M20,4 V20"),
        ["previous"] = Geometry.Parse("M20,4 L8,12 L20,20 Z M4,4 V20"),
        ["volume"] = Geometry.Parse("M3,9 H7 L12,4 V20 L7,15 H3 Z M16,7 Q22,12 16,17"),
        ["close"] = Geometry.Parse("M5,5 L19,19 M19,5 L5,19"),
        ["up"] = Geometry.Parse("M5,15 L12,8 L19,15"),
        ["down"] = Geometry.Parse("M5,9 L12,16 L19,9"),
        ["shuffle"] = Geometry.Parse("M3,5 H6 L18,19 H22 M18,15 L22,19 L18,23 M3,19 H6 L18,5 H22 M18,1 L22,5 L18,9"),
        ["repeat"] = Geometry.Parse("M4,8 V5 H20 L16,1 M20,16 V19 H4 L8,23 M20,5 V10 M4,19 V14")
    };
    static SwayIcon() => KindProperty.Changed.AddClassHandler<SwayIcon>((icon, _) => icon.Data = Shapes.GetValueOrDefault(icon.Kind, Shapes["music"]));
    public SwayIcon() { Data = Shapes["music"]; }
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
}
