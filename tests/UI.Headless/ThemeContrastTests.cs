using System.Globalization;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Theme contrast from the tokens (roadmap 4.5; WCAG 2.1 AA): text 4.5:1 (1.4.3), glyphs and indicators 3:1
/// (1.4.11). The pairs below AA are in <see cref="Exceptions"/> with their current value, which may not get worse; changing
/// them is a design decision (HeidiSQL highlight colours required by spec section 5.6, the phase 13 accent) recorded in the
/// roadmap. A pair that starts to comply leaves the exceptions.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double Text = 4.5;
    private const double Graphics = 3.0;

    /// <summary>Foreground, background, minimum and where it shows.</summary>
    private static readonly (string Foreground, string Background, double Minimum, string Where)[] Pairs =
    [
        ("text.primary", "surface.content", Text, "grid, tree, lists, editor"),
        ("text.primary", "surface.panel", Text, "panels"),
        ("text.primary", "surface.chrome", Text, "menus, toolbar, status bar"),
        ("text.primary", "surface.header", Text, "column headers"),
        ("text.primary", "surface.input", Text, "text boxes"),
        ("text.primary", "selection.row", Text, "selected row"),
        ("text.primary", "selection.inactive", Text, "unfocused selection"),
        ("text.primary", "hover", Text, "hover"),
        ("text.primary", "button.background", Text, "buttons"),
        ("text.primary", "button.hover", Text, "buttons on hover"),
        ("text.primary", "button.pressed", Text, "pressed buttons"),
        ("text.primary", "list.highlight", Text, "highlighted list rows"),
        ("text.primary", "log.error", Text, "SQL log: errors"),
        ("text.primary", "log.warning", Text, "SQL log: warnings"),
        ("text.primary", "log.note", Text, "SQL log: notes"),
        ("text.primary", "log.info", Text, "SQL log: information"),
        ("selection.focus.text", "selection.focus", Text, "focused node"),
        ("accent.text", "accent", Text, "focused grid cell"),
        ("text.muted", "surface.content", Text, "secondary text"),
        ("text.muted", "surface.panel", Text, "secondary text on panels"),
        ("text.muted", "surface.chrome", Text, "secondary text in the status bar"),
        ("sql.keyword", "surface.content", Text, "highlighting: keywords"),
        ("sql.datatype", "surface.content", Text, "highlighting: types"),
        ("sql.function", "surface.content", Text, "highlighting: functions"),
        ("sql.number", "surface.content", Text, "highlighting: numbers"),
        ("sql.string", "surface.content", Text, "highlighting: strings"),
        ("sql.comment", "surface.content", Text, "highlighting: comments"),
        ("sql.identifier", "surface.content", Text, "highlighting: identifiers"),
        ("sql.variable", "surface.content", Text, "highlighting: variables"),
        ("sql.gutter", "surface.content", Text, "editor line numbers"),
        ("type.integer", "surface.content", Text, "grid: integers"),
        ("type.real", "surface.content", Text, "grid: decimals"),
        ("type.text", "surface.content", Text, "grid: text"),
        ("type.binary", "surface.content", Text, "grid: binary"),
        ("type.temporal", "surface.content", Text, "grid: dates"),
        ("type.spatial", "surface.content", Text, "grid: spatial"),
        ("type.other", "surface.content", Text, "grid: other types"),
        ("grid.modified", "surface.content", Text, "changed values and error text"),
        ("log.gutter.text", "log.gutter", Text, "SQL log line numbers"),
        ("tree.glyph", "surface.panel", Graphics, "tree arrows"),
        ("tree.star", "surface.panel", Graphics, "favourites"),
        ("grid.key", "surface.content", Graphics, "key icon"),
        ("status.idle", "surface.chrome", Graphics, "status indicator"),
        ("status.busy", "surface.chrome", Graphics, "busy indicator"),
    ];

    /// <summary>Pairs below AA on 9 Oct 2026, with their current contrast (the author's decision in roadmap 4.5).</summary>
    private static readonly Dictionary<(string Theme, string Foreground, string Background), double> Exceptions = new()
    {
        [("Light", "accent.text", "accent")] = 4.28,
        [("Light", "sql.comment", "surface.content")] = 3.95,
        [("Light", "sql.identifier", "surface.content")] = 3.14,
        [("Light", "sql.variable", "surface.content")] = 4.20,
        [("Light", "sql.gutter", "surface.content")] = 2.52,
        [("Light", "type.other", "surface.content")] = 4.20,
        [("Light", "grid.modified", "surface.content")] = 3.91,
        [("Light", "log.gutter.text", "log.gutter")] = 2.37,
        [("Light", "tree.star", "surface.panel")] = 1.70,
        [("Light", "grid.key", "surface.content")] = 2.27,
        [("Dark", "accent.text", "accent")] = 4.28,
        [("Dark", "text.muted", "surface.panel")] = 4.26,
        [("Dark", "text.muted", "surface.chrome")] = 4.26,
        [("Dark", "sql.comment", "surface.content")] = 4.27,
        [("Dark", "sql.gutter", "surface.content")] = 3.46,
        [("Dark", "log.gutter.text", "log.gutter")] = 2.90,
    };

    [Fact]
    public void ThemePairsMeetAaOrDoNotGetWorse()
    {
        var problems = new List<string>();
        foreach (var theme in new[] { ThemeTokens.Light, ThemeTokens.Dark })
        {
            foreach (var (foreground, background, minimum, where) in Pairs)
            {
                Assert.True(theme.Tokens.ContainsKey(foreground) && theme.Tokens.ContainsKey(background), $"{theme.Name}: missing {foreground} or {background}");
                var ratio = Contrast(theme.Color(foreground), theme.Color(background));
                var shown = ratio.ToString("0.00", CultureInfo.InvariantCulture);
                if (Exceptions.TryGetValue((theme.Name, foreground, background), out var recorded))
                {
                    if (ratio >= minimum)
                    {
                        problems.Add($"{theme.Name} {foreground} on {background} ({where}): {shown} already complies; remove it from the exceptions");
                    }
                    else if (ratio < recorded - 0.005)
                    {
                        problems.Add($"{theme.Name} {foreground} on {background} ({where}): {shown} got worse (was {recorded:0.00})");
                    }
                }
                else if (ratio < minimum)
                {
                    problems.Add($"{theme.Name} {foreground} on {background} ({where}): {shown} < {minimum:0.0}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ContrastAsInWcag()
    {
        Assert.Equal(21.0, Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1.0, Contrast(Colors.Gray, Colors.Gray), 2);
        // #767676 on white is the lightest grey that meets 4.5:1.
        Assert.Equal(4.54, Contrast(Color.Parse("#767676"), Colors.White), 2);
    }

    /// <summary>WCAG 2.1 contrast ratio: (L1 + 0.05) / (L2 + 0.05) with sRGB relative luminance.</summary>
    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
