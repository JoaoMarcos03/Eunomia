using Avalonia.Media;
using Avalonia.Media.Immutable;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Controls.Tree;

/// <summary>Cores da árvore, lidas dos tokens do tema (<c>assets/themes</c>).</summary>
public sealed record TreePalette(
    IImmutableBrush Background,
    IImmutableBrush Foreground,
    IImmutableBrush Muted,
    IImmutableBrush Selection,
    IImmutableBrush SelectionForeground,
    IImmutableBrush Hover,
    IImmutableBrush SizeBar,
    IImmutableBrush Star,
    IImmutableBrush Glyph)
{
    /// <summary>
    /// Cor do texto dos nós de coluna por categoria de tipo (anexo D A2: "a mesma paleta do grid"; ordem de
    /// <c>GridPalette</c>: inteiro, real, texto, binário, data/hora, espacial, outro).
    /// </summary>
    public IReadOnlyList<IImmutableBrush> Categories { get; init; } = [];

    /// <summary>Nó focado quando a árvore não tem o foco do teclado.</summary>
    public IImmutableBrush InactiveSelection { get; init; } = Brush(0xFFE6E8EC);

    /// <summary>Fundo em ARGB (para a tinta da cor da sessão).</summary>
    public uint BackgroundArgb { get; init; } = 0xFFFFFFFF;

    public static TreePalette Light { get; } = From(ThemeTokens.Light);

    public static TreePalette Dark { get; } = From(ThemeTokens.Dark);

    private static TreePalette From(ThemeFile t) => new(
        Brush(t.Argb("surface.content")), Brush(t.Argb("text.primary")), Brush(t.Argb("text.muted")), Brush(t.Argb("selection.focus")),
        Brush(t.Argb("selection.focus.text")), Brush(t.Argb("hover")), Brush(t.Argb("tree.sizebar")), Brush(t.Argb("tree.star")), Brush(t.Argb("tree.glyph")))
    {
        Categories = [.. new[] { "type.integer", "type.real", "type.text", "type.binary", "type.temporal", "type.spatial", "type.other" }.Select(k => Brush(t.Argb(k)))],
        InactiveSelection = Brush(t.Argb("selection.inactive")),
        BackgroundArgb = t.Argb("surface.content"),
    };

    private static ImmutableSolidColorBrush Brush(uint argb) => new(argb);
}
