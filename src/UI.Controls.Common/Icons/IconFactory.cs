using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Eunomia.UI.Controls.Common.Icons;

/// <summary>
/// Cria imagens dos ícones do <see cref="IconSet"/> (com cache). A versão desativada é gerada em tons de cinzento,
/// como no HeidiSQL (secção 5.7: "ícones em versão grayscale gerada"). No tema escuro (<see cref="Dark"/>) as tintas
/// escuras (contornos cinzentos, linhas azul-escuras) são aclaradas para se verem no fundo escuro.
/// </summary>
public static class IconFactory
{
    private static readonly Dictionary<(string, bool, bool), IImage?> Cache = [];

    /// <summary>Tema escuro ativo (a aplicação tem um só tema para todas as janelas).</summary>
    public static bool Dark { get; private set; }

    /// <summary>O tema dos ícones mudou: quem guardou imagens deve pedi-las de novo.</summary>
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
            // Moldura transparente 16×16: todos os ícones ficam com o mesmo tamanho lógico.
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

    /// <summary>Controlo <see cref="Image"/> 16×16 com o ícone (ou <c>null</c> se a chave não existir).</summary>
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
                // Tinta escura no fundo escuro: 60% do caminho até ao branco, mantendo o tom.
                static byte Lighten(byte c) => (byte)(c + ((255 - c) * 0.6));
                return Color.FromRgb(Lighten(r), Lighten(g), Lighten(b));
            }
            return Color.FromRgb(r, g, b);
        }
        // Luminância para a versão desativada: clara no tema claro, apagada no escuro.
        var level = dark ? (byte)(60 + (y * 0.25)) : (byte)(y + ((255 - y) * 0.45));
        return Color.FromRgb(level, level, level);
    }
}
