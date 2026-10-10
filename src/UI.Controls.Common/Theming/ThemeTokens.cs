using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Eunomia.UI.Controls.Common.Theming;

/// <summary>Token file of a theme (<c>assets/themes/*.json</c>): token name → <c>#RRGGBB</c> colour.</summary>
public sealed class ThemeFile
{
    public string Name { get; set; } = string.Empty;

    public string? Comment { get; set; }

    public Dictionary<string, string> Tokens { get; set; } = [];

    /// <summary>Colour of a token (magenta if missing, so it shows right away in the snapshots).</summary>
    public Color Color(string token) => Tokens.TryGetValue(token, out var hex) ? Avalonia.Media.Color.Parse(hex) : Colors.Magenta;

    /// <summary>Colour of a token as ARGB (<c>0xAARRGGBB</c>), for controls that draw themselves.</summary>
    public uint Argb(string token) => Color(token).ToUInt32();
}

/// <summary>Colour blends for tints (session colour over the background) and text contrast.</summary>
public static class ColorMath
{
    /// <summary>Blends <paramref name="color"/> with <paramref name="background"/>: 0 = background only, 1 = colour only.</summary>
    public static uint Mix(uint color, uint background, double amount)
    {
        uint Channel(int shift) => (uint)Math.Round((((color >> shift) & 0xFF) * amount) + (((background >> shift) & 0xFF) * (1 - amount)));
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    /// <summary>Perceived luminance below half (light text on top).</summary>
    public static bool IsDark(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (0.299 * r) + (0.587 * g) + (0.114 * b) < 128;
    }

    /// <summary>Tint of the session colour over a background (tree rows, grid gutter): soft in light and dark.</summary>
    public static uint SessionTint(uint sessionColor, uint background) =>
        Mix(sessionColor | 0xFF000000, background, IsDark(background) ? 0.32 : 0.22);
}

/// <summary>
/// Light and dark theme tokens (section 5.6; section 21.7: themes are data). Each token becomes a
/// <see cref="SolidColorBrush"/> in the application's <c>ThemeDictionaries</c>, with the key <c>eu.&lt;token&gt;</c>;
/// controls bind with <see cref="Themed.Bind"/> and change on their own when the theme changes.
/// </summary>
public static class ThemeTokens
{
    public const string KeyPrefix = "eu.";

    public static ThemeFile Light { get; } = Load("light");

    public static ThemeFile Dark { get; } = Load("dark");

    public static string Key(string token) => KeyPrefix + token;

    /// <summary>Tokens of the variant (dark only when the variant is <see cref="ThemeVariant.Dark"/>).</summary>
    public static ThemeFile Of(ThemeVariant? variant) => variant == ThemeVariant.Dark ? Dark : Light;

    /// <summary>Installs the light and dark tokens in the application resources.</summary>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Resources.ThemeDictionaries[ThemeVariant.Light] = ToDictionary(Light);
        application.Resources.ThemeDictionaries[ThemeVariant.Dark] = ToDictionary(Dark);
    }

    /// <summary>Colour of a token for a variant (for controls that draw themselves).</summary>
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

/// <summary>Binds control properties to theme tokens (updated when the variant changes).</summary>
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
