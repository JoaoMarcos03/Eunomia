using Avalonia.Media;
using Avalonia.Media.Immutable;
using Eunomia.Engine.Results;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Grid;

/// <summary>
/// Cores do grid, lidas dos tokens do tema (<c>assets/themes</c>). Cores de dados por categoria: secção 5.6
/// (<c>preferences.pas</c>). NULL: cor da categoria clareada (+70 por canal) no claro; no escuro, misturada 45% com o
/// fundo (clarear cores já claras deixava o NULL igual ao texto normal; escurecer 70 tirava o contraste).
/// </summary>
public sealed class GridPalette
{
    private static readonly string[] CategoryTokens = ["type.integer", "type.real", "type.text", "type.binary", "type.temporal", "type.spatial", "type.other"];

    private readonly IImmutableBrush[] _category;
    private readonly IImmutableBrush[] _null;

    private GridPalette(ThemeFile theme, bool dark)
    {
        IsDark = dark;
        var background = theme.Argb("surface.content");
        var categories = CategoryTokens.Select(theme.Argb).ToArray();
        _category = categories.Select(c => (IImmutableBrush)new ImmutableSolidColorBrush(c)).ToArray();
        _null = categories.Select(c => (IImmutableBrush)new ImmutableSolidColorBrush(dark ? Mix(c, background, 0.45) : Shift(c, 70))).ToArray();
        BackgroundArgb = background;
        Background = Brush(background);
        Header = Brush(theme.Argb("surface.header"));
        HeaderText = Brush(theme.Argb("text.primary"));
        Muted = Brush(theme.Argb("text.muted"));
        GridLine = new ImmutablePen(theme.Argb("grid.line"), 1);
        SelectedRow = Brush(theme.Argb("selection.row"));
        FocusedCell = Brush(theme.Argb("accent"));
        FocusedText = Brush(theme.Argb("accent.text"));
        InactiveCell = Brush(theme.Argb("selection.focus"));
        RowNumberArgb = theme.Argb("surface.header");
        RowNumberBackground = Brush(RowNumberArgb);
        Modified = Brush(theme.Argb("grid.modified"));
        KeyIcon = Brush(theme.Argb("grid.key"));
    }

    public static GridPalette Light { get; } = new(ThemeTokens.Light, dark: false);

    public static GridPalette Dark { get; } = new(ThemeTokens.Dark, dark: true);

    public bool IsDark { get; }

    public IImmutableBrush Background { get; }

    public IImmutableBrush Header { get; }

    public IImmutableBrush HeaderText { get; }

    public IImmutableBrush Muted { get; }

    public ImmutablePen GridLine { get; }

    public IImmutableBrush SelectedRow { get; }

    public IImmutableBrush FocusedCell { get; }

    public IImmutableBrush FocusedText { get; }

    /// <summary>Célula focada com o grid sem foco: a cor de seleção suave, com o texto na cor do tipo.</summary>
    public IImmutableBrush InactiveCell { get; }

    public IImmutableBrush RowNumberBackground { get; }

    /// <summary>Fundo das células e da coluna <c>#</c> em ARGB (para a tinta da cor da sessão).</summary>
    public uint BackgroundArgb { get; }

    public uint RowNumberArgb { get; }

    public IImmutableBrush Modified { get; }

    public IImmutableBrush KeyIcon { get; }

    /// <summary>Cor do texto por categoria de tipo (ordem de <see cref="TypeCategory"/>).</summary>
    public IImmutableBrush TextFor(TypeCategory category) => _category[(int)category];

    public IImmutableBrush NullFor(TypeCategory category) => _null[(int)category];

    private static ImmutableSolidColorBrush Brush(uint argb) => new(argb);

    private static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round((((a >> shift) & 0xFF) * (1 - t)) + (((b >> shift) & 0xFF) * t));
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static uint Shift(uint argb, int delta)
    {
        static uint Channel(uint v, int d) => (uint)Math.Clamp((int)v + d, 0, 255);
        var r = Channel((argb >> 16) & 0xFF, delta);
        var g = Channel((argb >> 8) & 0xFF, delta);
        var b = Channel(argb & 0xFF, delta);
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }
}
