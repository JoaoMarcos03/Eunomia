using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.Icons;

/// <summary>
/// Creates images of the <see cref="IconSet"/> icons (cached). The disabled version is generated in shades of grey,
/// as in HeidiSQL (section 5.7: "icons in a generated grayscale version"). In the dark theme (<see cref="Dark"/>) the dark
/// inks (grey outlines, dark blue lines) are lightened so they show on the dark background.
/// </summary>
public static class IconFactory
{
    private static readonly Dictionary<(string, bool, bool), IImage?> Cache = [];

    /// <summary>Dark theme active (the application has a single theme for every window).</summary>
    public static bool Dark { get; private set; }

    /// <summary>The icon theme changed: whoever kept images must request them again.</summary>
    public static event EventHandler? ThemeChanged;

    public static void SetDark(bool dark)
    {
        if (Dark != dark)
        {
            Dark = dark;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static IImage? Get(string? key, bool enabled = true)
    {
        if (key is null)
        {
            return null;
        }
        var dark = Dark;
        if (Cache.TryGetValue((key, enabled, dark), out var cached))
        {
            return cached;
        }
        var parts = IconSet.Get(key);
        IImage? image = null;
        if (parts is not null)
        {
            var group = new DrawingGroup();
            // Transparent 16×16 frame: every icon gets the same logical size.
            group.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)), Brush = Brushes.Transparent });
            foreach (var part in parts)
            {
                group.Children.Add(new GeometryDrawing
                {
                    Geometry = StreamGeometry.Parse(part.Path),
                    Brush = part.Fill is { } fill ? new SolidColorBrush(ToColor(fill, enabled, dark)) : null,
                    Pen = part.Stroke is { } stroke ? new Pen(new SolidColorBrush(ToColor(stroke, enabled, dark)), part.StrokeWidth) : null,
                });
            }
            image = new DrawingImage(group);
        }
        Cache[(key, enabled, dark)] = image;
        return image;
    }

    /// <summary>16×16 <see cref="Image"/> control with the icon (or <c>null</c> if the key does not exist).</summary>
    public static Image? CreateImage(string? key, bool enabled = true)
    {
        var source = Get(key, enabled);
        return source is null ? null : new Image { Source = source, Width = 16, Height = 16 };
    }

    private static Color ToColor(uint rgb, bool enabled, bool dark)
    {
        var r = (byte)(rgb >> 16);
        var g = (byte)(rgb >> 8);
        var b = (byte)rgb;
        var y = Math.Min(255, (0.299 * r) + (0.587 * g) + (0.114 * b));
        if (enabled)
        {
            if (dark && y < 110)
            {
                // Dark ink on a dark background: 60% of the way to white, keeping the hue.
                static byte Lighten(byte c) => (byte)(c + ((255 - c) * 0.6));
                return Color.FromRgb(Lighten(r), Lighten(g), Lighten(b));
            }
            return Color.FromRgb(r, g, b);
        }
        // Luminance for the disabled version: light in the light theme, dimmed in the dark one.
        var level = dark ? (byte)(60 + (y * 0.25)) : (byte)(y + ((255 - y) * 0.45));
        return Color.FromRgb(level, level, level);
    }
}
