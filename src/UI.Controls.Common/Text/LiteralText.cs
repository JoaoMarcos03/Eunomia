using Avalonia.Controls;
using Avalonia.Layout;

namespace Eunomia.UI.Controls.Common.Text;

/// <summary>
/// Content of buttons, check boxes and menu items with text that comes from data (table, column and database names, values,
/// files): a plain string goes through the theme's AccessText and the first <c>_</c> becomes an access key and disappears
/// ("prd_products" showed as "prdproducts"). A <see cref="TextBlock"/> shows the text as it is.
/// </summary>
public static class LiteralText
{
    public static TextBlock Of(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// For a <c>Header</c> or <c>Content</c> that has to stay a string (menus): each <c>_</c> becomes <c>__</c>, which
    /// AccessText draws as a <c>_</c>. Never for reading the value back: the value stays in the variable or in <c>Tag</c>.
    /// </summary>
    public static string Escaped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("_", "__", StringComparison.Ordinal);
    }
}
