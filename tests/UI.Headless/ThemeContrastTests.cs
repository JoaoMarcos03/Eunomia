using System.Globalization;
using Avalonia.Media;
using Eunomia.UI.Controls.Common.Theming;

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Contraste dos temas a partir dos tokens (roadmap 4.5; WCAG 2.1 AA): texto 4,5:1 (1.4.3), glifos e indicadores 3:1
/// (1.4.11). Os pares abaixo do AA estão em <see cref="Exceptions"/> com o valor atual, que não pode piorar; mudá-los é
/// uma decisão de design (cores do realce do HeidiSQL exigidas pela secção 5.6 da spec, acento da Fase 13) registada no
/// roadmap. Um par que passe a cumprir sai das exceções.
/// </summary>
public sealed class ThemeContrastTests
{
    private const double Text = 4.5;
    private const double Graphics = 3.0;

    /// <summary>Primeiro plano, fundo, mínimo e onde se vê.</summary>
    private static readonly (string Foreground, string Background, double Minimum, string Where)[] Pairs =
    [
        ("text.primary", "surface.content", Text, "grid, árvore, listas, editor"),
        ("text.primary", "surface.panel", Text, "painéis"),
        ("text.primary", "surface.chrome", Text, "menus, toolbar, status bar"),
        ("text.primary", "surface.header", Text, "cabeçalhos das colunas"),
        ("text.primary", "surface.input", Text, "caixas de texto"),
        ("text.primary", "selection.row", Text, "linha selecionada"),
        ("text.primary", "selection.inactive", Text, "seleção sem foco"),
        ("text.primary", "hover", Text, "hover"),
        ("text.primary", "button.background", Text, "botões"),
        ("text.primary", "button.hover", Text, "botões em hover"),
        ("text.primary", "button.pressed", Text, "botões premidos"),
        ("text.primary", "list.highlight", Text, "linhas realçadas das listas"),
        ("text.primary", "log.error", Text, "SQL log: erros"),
        ("text.primary", "log.warning", Text, "SQL log: avisos"),
        ("text.primary", "log.note", Text, "SQL log: notas"),
        ("text.primary", "log.info", Text, "SQL log: informação"),
        ("selection.focus.text", "selection.focus", Text, "nó focado"),
        ("accent.text", "accent", Text, "célula focada do grid"),
        ("text.muted", "surface.content", Text, "texto secundário"),
        ("text.muted", "surface.panel", Text, "texto secundário nos painéis"),
        ("text.muted", "surface.chrome", Text, "texto secundário na status bar"),
        ("sql.keyword", "surface.content", Text, "realce: keywords"),
        ("sql.datatype", "surface.content", Text, "realce: tipos"),
        ("sql.function", "surface.content", Text, "realce: funções"),
        ("sql.number", "surface.content", Text, "realce: números"),
        ("sql.string", "surface.content", Text, "realce: strings"),
        ("sql.comment", "surface.content", Text, "realce: comentários"),
        ("sql.identifier", "surface.content", Text, "realce: identificadores"),
        ("sql.variable", "surface.content", Text, "realce: variáveis"),
        ("sql.gutter", "surface.content", Text, "números de linha do editor"),
        ("type.integer", "surface.content", Text, "grid: inteiros"),
        ("type.real", "surface.content", Text, "grid: decimais"),
        ("type.text", "surface.content", Text, "grid: texto"),
        ("type.binary", "surface.content", Text, "grid: binário"),
        ("type.temporal", "surface.content", Text, "grid: datas"),
        ("type.spatial", "surface.content", Text, "grid: espaciais"),
        ("type.other", "surface.content", Text, "grid: outros tipos"),
        ("grid.modified", "surface.content", Text, "valores alterados e texto de erro"),
        ("log.gutter.text", "log.gutter", Text, "números de linha do SQL log"),
        ("tree.glyph", "surface.panel", Graphics, "setas da árvore"),
        ("tree.star", "surface.panel", Graphics, "favoritos"),
        ("grid.key", "surface.content", Graphics, "ícone de chave"),
        ("status.idle", "surface.chrome", Graphics, "indicador de estado"),
        ("status.busy", "surface.chrome", Graphics, "indicador de ocupado"),
    ];

    /// <summary>Pares abaixo do AA a 9 out 2026, com o contraste atual (decisão do autor no roadmap 4.5).</summary>
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
    public void ParesDosTemasCumpremOAaOuNaoPioram()
    {
        var problems = new List<string>();
        foreach (var theme in new[] { ThemeTokens.Light, ThemeTokens.Dark })
        {
            foreach (var (foreground, background, minimum, where) in Pairs)
            {
                Assert.True(theme.Tokens.ContainsKey(foreground) && theme.Tokens.ContainsKey(background), $"{theme.Name}: falta {foreground} ou {background}");
                var ratio = Contrast(theme.Color(foreground), theme.Color(background));
                var shown = ratio.ToString("0.00", CultureInfo.InvariantCulture);
                if (Exceptions.TryGetValue((theme.Name, foreground, background), out var recorded))
                {
                    if (ratio >= minimum)
                    {
                        problems.Add($"{theme.Name} {foreground} sobre {background} ({where}): {shown} já cumpre; tirar das exceções");
                    }
                    else if (ratio < recorded - 0.005)
                    {
                        problems.Add($"{theme.Name} {foreground} sobre {background} ({where}): {shown} piorou (era {recorded:0.00})");
                    }
                }
                else if (ratio < minimum)
                {
                    problems.Add($"{theme.Name} {foreground} sobre {background} ({where}): {shown} < {minimum:0.0}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ContrasteComoNaWcag()
    {
        Assert.Equal(21.0, Contrast(Colors.Black, Colors.White), 2);
        Assert.Equal(1.0, Contrast(Colors.Gray, Colors.Gray), 2);
        // #767676 sobre branco é o cinzento mais claro que cumpre 4,5:1.
        Assert.Equal(4.54, Contrast(Color.Parse("#767676"), Colors.White), 2);
    }

    /// <summary>Razão de contraste da WCAG 2.1: (L1 + 0,05) / (L2 + 0,05) com a luminância relativa sRGB.</summary>
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
