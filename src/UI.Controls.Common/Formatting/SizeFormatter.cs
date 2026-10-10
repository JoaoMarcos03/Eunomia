using System.Globalization;

namespace Eunomia.UI.Controls.Common.Formatting;

/// <summary>
/// Sizes like HeidiSQL's <c>FormatByteNumber</c> (appendix D A8): one decimal place, locale separator, suffixes
/// B/KiB/MiB/GiB/TiB. The unit changes at the 1000 thresholds but divides by 1024, so 1000 bytes give "1.0 KiB".
/// </summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        for (var unit = Units.Length - 1; unit > 0; unit--)
        {
            if (bytes >= Math.Pow(1000, unit))
            {
                return (bytes / Math.Pow(1024, unit)).ToString("0.0", culture) + " " + Units[unit];
            }
        }
        return bytes.ToString("0", culture) + " B";
    }
}
