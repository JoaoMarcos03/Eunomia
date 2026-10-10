using System.Globalization;

namespace Eunomia.UI.Controls.Common.Formatting;

/// <summary>
/// Tamanhos como o <c>FormatByteNumber</c> do HeidiSQL (anexo D A8): uma casa decimal, separador do locale, sufixos
/// B/KiB/MiB/GiB/TiB. A unidade muda nos limiares de 1000 mas divide por 1024, por isso 1000 bytes dão "1,0 KiB".
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
