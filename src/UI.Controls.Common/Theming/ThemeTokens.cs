using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Eunomia.UI.Controls.Common.Theming;

/// <summary>Ficheiro de tokens de um tema (<c>assets/themes/*.json</c>): nome do token → cor <c>#RRGGBB</c>.</summary>
public sealed class ThemeFile
{
    public string Name { get; set; } = string.Empty;

    public string? Comment { get; set; }

    public Dictionary<string, string> Tokens { get; set; } = [];

    /// <summary>Cor de um token (magenta se faltar, para se ver logo nos snapshots).</summary>
    public Color Color(string token) => Tokens.TryGetValue(token, out var hex) ? Avalonia.Media.Color.Parse(hex) : Colors.Magenta;

    /// <summary>Cor de um token em ARGB (<c>0xAARRGGBB</c>), para os controlos que desenham sozinhos.</summary>
    public uint Argb(string token) => Color(token).ToUInt32();
}

/// <summary>Misturas de cores para tintas (cor da sessão sobre o fundo) e contraste do texto.</summary>
public static class ColorMath
{
    /// <summary>Mistura <paramref name="color"/> com <paramref name="background"/>: 0 = só o fundo, 1 = só a cor.</summary>
    public static uint Mix(uint color, uint background, double amount)
    {
        uint Channel(int shift) => (uint)Math.Round((((color >> shift) & 0xFF) * amount) + (((background >> shift) & 0xFF) * (1 - amount)));
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>Luminância percebida abaixo de metade (texto claro por cima).</summary>
    public static bool IsDark(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (0.299 * r) + (0.587 * g) + (0.114 * b) < 128;
    }

    /// <summary>Tinta da cor da sessão sobre um fundo (linhas da árvore, gutter do grid): suave no claro e no escuro.</summary>
    public static uint SessionTint(uint sessionColor, uint background) =>
        Mix(sessionColor | 0xFF000000, background, IsDark(background) ? 0.32 : 0.22);
}

/// <summary>
/// Tokens de tema claro e escuro (secção 5.6; secção 21.7: temas são dados). Cada token vira um
/// <see cref="SolidColorBrush"/> nos <c>ThemeDictionaries</c> da aplicação, com a chave <c>eu.&lt;token&gt;</c>;
/// os controlos ligam-se com <see cref="Themed.Bind"/> e mudam sozinhos quando o tema muda.
/// </summary>
public static class ThemeTokens
{
    public const string KeyPrefix = "eu.";

    public static ThemeFile Light { get; } = Load("light");

    public static ThemeFile Dark { get; } = Load("dark");

    public static string Key(string token) => KeyPrefix + token;

    /// <summary>Tokens da variante (escuro só quando a variante é <see cref="ThemeVariant.Dark"/>).</summary>
    public static ThemeFile Of(ThemeVariant? variant) => variant == ThemeVariant.Dark ? Dark : Light;

    /// <summary>Instala os tokens claro e escuro nos recursos da aplicação.</summary>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Resources.ThemeDictionaries[ThemeVariant.Light] = ToDictionary(Light);
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = ToDictionary(Dark);
    }

    /// <summary>Cor de um token para uma variante (para controlos que desenham sozinhos).</summary>
    public static Color ColorOf(string token, ThemeVariant? variant) => Of(variant).Color(token);

    private static ResourceDictionary ToDictionary(ThemeFile file)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (token, hex) in file.Tokens)
        {
            dictionary[Key(token)] = new SolidColorBrush(Color.Parse(hex));
        }
        return dictionary;
    }

    private static ThemeFile Load(string name)
    {
        using var stream = typeof(ThemeTokens).Assembly.GetManifestResourceStream($"Eunomia.Themes.{name}.json")
            ?? throw new InvalidOperationException($"Tema embebido em falta: {name}.");
        return JsonSerializer.Deserialize(stream, ThemeJsonContext.Default.ThemeFile)
            ?? throw new InvalidOperationException($"Tema inválido: {name}.");
    }
}

/// <summary>Liga propriedades de controlos a tokens de tema (atualizadas quando a variante muda).</summary>
public static class Themed
{
    public static T Bind<T>(T control, AvaloniaProperty property, string token)
        where T : Control
    {
        control.Bind(property, control.GetResourceObservable(ThemeTokens.Key(token)));
        return control;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ThemeFile))]
internal sealed partial class ThemeJsonContext : JsonSerializerContext;
