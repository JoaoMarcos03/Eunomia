using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Eunomia.UI.Controls.Common.Icons;
using Eunomia.UI.Controls.Common.Theming;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Shell;

/// <summary>
/// The application's look (phase 13), shared by the app and the headless tests so the snapshots show what the
/// user sees: Fluent in compact density (HeidiSQL is a dense application, section 5.6) with the theme token colours,
/// Segoe UI 9 pt, and global styles for buttons, text boxes, tabs, menus and tooltips.
/// </summary>
public static class ShellTheme
{
    public const double FontSize = 12;

    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var fluent = new FluentTheme(null!) { DensityStyle = DensityStyle.Compact };
        fluent.Palettes[ThemeVariant.Light] = Palette(ThemeTokens.Light, dark: false);
        fluent.Palettes[ThemeVariant.Dark] = Palette(ThemeTokens.Dark, dark: true);
        application.Styles.Add(fluent);
        application.Styles.Add(new SqlEditorStyles());
        application.Styles.Add(GlobalStyles());
        ThemeTokens.Install(application);
        AddFluentOverrides(application, ThemeVariant.Light, ThemeTokens.Light);
        AddFluentOverrides(application, ThemeVariant.Dark, ThemeTokens.Dark);
        application.ActualThemeVariantChanged += (_, _) => IconFactory.SetDark(application.ActualThemeVariant == ThemeVariant.Dark);
    }

    /// <summary>
    /// Fluent resources the palette does not cover: light outlined buttons (instead of translucent grey), with
    /// hover and pressed from the tokens. They live in the application resources, which win over the styles'.
    /// </summary>
    private static void AddFluentOverrides(Application application, ThemeVariant variant, ThemeFile t)
    {
        if (application.Resources.ThemeDictionaries[variant] is not ResourceDictionary resources)
        {
            return;
        }
        SolidColorBrush B(string token) => new(t.Color(token));
        resources["ButtonBackground"] = B("button.background");
        resources["ButtonBackgroundPointerOver"] = B("button.hover");
        resources["ButtonBackgroundPressed"] = B("button.pressed");
        resources["ButtonBackgroundDisabled"] = B("surface.panel");
        resources["ButtonBorderBrush"] = B("button.border");
        resources["ButtonBorderBrushPointerOver"] = B("button.border");
        resources["ButtonBorderBrushPressed"] = B("button.border");
        resources["ButtonBorderBrushDisabled"] = B("border");
        resources["ButtonForegroundDisabled"] = B("text.disabled");
    }

    /// <summary>
    /// Fluent colours from the tokens: backgrounds (Alt*), text (Base*), flyouts and highlights (Chrome*, List*) and accent,
    /// so the Fluent controls get the same surfaces as the hand-drawn controls.
    /// </summary>
    private static ColorPaletteResources Palette(ThemeFile t, bool dark)
    {
        var content = t.Color("surface.content");
        var panel = t.Color("surface.panel");
        var text = t.Color("text.primary");
        var border = t.Color("border");
        Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        return new ColorPaletteResources
        {
            Accent = t.Color("accent"),
            AltHigh = content,
            AltMediumHigh = Alpha(content, 0xCC),
            AltMedium = Alpha(content, 0x99),
            AltMediumLow = Alpha(content, 0x66),
            AltLow = Alpha(content, 0x33),
            BaseHigh = text,
            BaseMediumHigh = Alpha(text, 0xCC),
            BaseMedium = Alpha(text, 0x99),
            BaseMediumLow = Alpha(text, 0x66),
            BaseLow = Alpha(text, 0x33),
            ChromeAltLow = Alpha(text, 0xCC),
            ChromeHigh = border,
            ChromeMedium = panel,
            ChromeMediumLow = dark ? panel : content,
            ChromeLow = panel,
            ChromeGray = t.Color("text.muted"),
            ChromeDisabledHigh = border,
            ChromeDisabledLow = t.Color("text.disabled"),
            ListLow = t.Color("hover"),
            ListMedium = t.Color("selection.inactive"),
            RegionColor = panel,
            ErrorText = t.Color("grid.modified"),
        };
    }

    private static Styles GlobalStyles()
    {
        var styles = new Styles
        {
            // Windows and dialogs: Segoe UI 9 pt and the panel background.
            new Style(x => x.OfType<Window>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontFamilyProperty, UiFonts.Ui),
                    new Setter(TemplatedControl.FontSizeProperty, FontSize),
                    new Setter(TemplatedControl.BackgroundProperty, new DynamicResourceExtension(ThemeTokens.Key("surface.panel"))),
                },
            },
            // Buttons: corners and the height of a form row.
            new Style(x => x.OfType<Button>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                    new Setter(Layoutable.MinHeightProperty, 24.0),
                },
            },
            new Style(x => x.OfType<TextBox>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                    new Setter(Layoutable.MinHeightProperty, 24.0),
                },
            },
            new Style(x => x.OfType<ComboBox>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4)),
                    new Setter(Layoutable.MinHeightProperty, 24.0),
                },
            },
            new Style(x => x.OfType<TabItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, FontSize),
                    new Setter(Layoutable.MinHeightProperty, 26.0),
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)),
                },
            },
            new Style(x => x.OfType<MenuItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.FontSizeProperty, FontSize),
                    new Setter(Layoutable.MinHeightProperty, 24.0),
                },
            },
            new Style(x => x.OfType<ToolTip>())
            {
                Setters = { new Setter(TemplatedControl.FontSizeProperty, FontSize) },
            },
        };
        return styles;
    }
}
