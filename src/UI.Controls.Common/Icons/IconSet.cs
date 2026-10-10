using System.Globalization;
using System.Text;

namespace Eunomia.UI.Controls.Common.Icons;

/// <summary>Uma camada de um ícone: geometria (path data 16×16), preenchimento e traço (0xRRGGBB).</summary>
public sealed record IconPart(string Path, uint? Fill, uint? Stroke = null, double StrokeWidth = 1);

/// <summary>
/// Icon set próprio (D12: mesma semântica, tamanho 16×16 e cor dominante do HeidiSQL, sem Icons8). Glifos
/// vetoriais simples desenhados para este projeto. Chave desconhecida = sem ícone.
/// </summary>
public static class IconSet
{
    private const uint Blue = 0x2F6FC9;
    private const uint DarkBlue = 0x1F4E8C;
    private const uint Green = 0x2E8B3A;
    private const uint Red = 0xC62828;
    private const uint Orange = 0xE3A33B;
    private const uint DarkOrange = 0xA9741F;
    private const uint Gray = 0x6E6E6E;
    private const uint Paper = 0xFFFFFF;
    private const uint Yellow = 0xF2B800;
    private const uint Purple = 0x7B4FB5;

    private static readonly Dictionary<string, IconPart[]> Icons = Build();

    public static IEnumerable<string> Keys => Icons.Keys;

    public static IReadOnlyList<IconPart>? Get(string? key) => key is not null && Icons.TryGetValue(key, out var parts) ? parts : null;

    private static IconPart P(string path, uint? fill, uint? stroke = null, double width = 1) => new(path, fill, stroke, width);

    private static Dictionary<string, IconPart[]> Build()
    {
        var doc = P("M3.5,1.5 H10 L13.5,5 V14.5 H3.5 Z", Paper, Gray);
        var floppy = new[]
        {
            P("M1.5,1.5 H12 L14.5,4 V14.5 H1.5 Z", Blue, DarkBlue),
            P("M4,1.5 H11 V5.5 H4 Z", Paper),
            P("M3.5,9 H12.5 V14.5 H3.5 Z", Paper),
        };
        var magnifier = new[]
        {
            P("M6.5,1.5 A5,5 0 1 1 6.49,1.5 Z", 0xDDEBFA, DarkBlue, 1.5),
            P("M10.2,10.2 L14.5,14.5", null, Gray, 2.5),
        };
        var table = new[]
        {
            P("M1.5,2.5 H14.5 V13.5 H1.5 Z", Paper, 0x5B7BB5),
            P("M1.5,2.5 H14.5 V5.5 H1.5 Z", 0x5B7BB5),
            P("M1.5,9.5 H14.5 M6,5.5 V13.5 M10.5,5.5 V13.5", null, 0x5B7BB5),
        };
        var cylinder = new[]
        {
            P("M2,3.5 C2,1.5 14,1.5 14,3.5 V12.5 C14,14.5 2,14.5 2,12.5 Z", Orange, DarkOrange),
            P("M2,3.5 C2,5.5 14,5.5 14,3.5", null, DarkOrange),
        };
        var funnel = P("M1,2 H15 L9.5,8 V14 L6.5,12.5 V8 Z", Blue, DarkBlue);
        var redX = P("M10,1 L15,6 M15,1 L10,6", null, Red, 1.8);
        var play = P("M3,1.5 V14.5 L14,8 Z", Green, 0x1E6B2A);

        return new Dictionary<string, IconPart[]>(StringComparer.Ordinal)
        {
            ["session-manager"] =
            [
                P("M1.5,1.5 H14.5 V6.5 H1.5 Z", 0x5B8DD6, DarkBlue), P("M1.5,9.5 H14.5 V14.5 H1.5 Z", 0x5B8DD6, DarkBlue),
                P("M3.5,3.5 H5.5 V4.5 H3.5 Z M3.5,11.5 H5.5 V12.5 H3.5 Z", 0x9CFF9C),
            ],
            ["disconnect"] =
            [
                P("M1,9 H4 M4,6 V12 H7 A3,3 0 0 0 7,6 Z M10,9 H12", Gray, 0x404040), redX,
            ],
            ["new-window"] = [P("M4.5,1.5 H14.5 V10.5 H4.5 Z", Paper, Blue), P("M4.5,1.5 H14.5 V3.5 H4.5 Z", Blue), P("M1.5,5.5 H11.5 V14.5 H1.5 Z", Paper, Blue), P("M1.5,5.5 H11.5 V7.5 H1.5 Z", Blue)],
            ["exit"] = [P("M2.5,1.5 H10.5 V14.5 H2.5 Z", 0xC8A26B, 0x8A6A3A), P("M7,8 H15 M12,5 L15,8 L12,11", null, Red, 1.6)],
            ["copy"] = [P("M5.5,1.5 H11 L13.5,4 V11.5 H5.5 Z", Paper, Gray), P("M2.5,4.5 H8 L10.5,7 V14.5 H2.5 Z", Paper, Gray), P("M4,9 H9 M4,11 H9", null, 0x9A9A9A)],
            ["cut"] = [P("M5,1 L10,11 M11,1 L6,11", null, 0x404040, 1.4), P("M3.5,10 A2.2,2.2 0 1 1 3.49,10 Z M12.5,10 A2.2,2.2 0 1 1 12.49,10 Z", null, Red, 1.4)],
            ["paste"] = [P("M2.5,2.5 H13.5 V14.5 H2.5 Z", 0xC8A26B, 0x8A6A3A), P("M5.5,1 H10.5 V4 H5.5 Z", Gray), P("M5.5,6.5 H12.5 V13.5 H5.5 Z", Paper, Gray)],
            ["undo"] = [P("M2,6 L6,2 V4.5 C10.5,4.5 14,6.5 14,11.5 C12.5,9 10,7.5 6,7.5 V10 Z", Blue, DarkBlue)],
            ["print"] = [P("M4.5,1.5 H11.5 V5.5 H4.5 Z", Paper, Gray), P("M1.5,5.5 H14.5 V11.5 H1.5 Z", 0x8A8A8A, 0x505050), P("M4.5,9.5 H11.5 V14.5 H4.5 Z", Paper, Gray)],
            ["refresh"] = [P("M13,8 A5,5 0 1 1 11.6,4.4 L9.6,6.4 H15 V1 L13,3 A7,7 0 1 0 15,8 Z", Green, 0x1E6B2A, 0.5)],
            ["user-manager"] = [P("M8,1.5 A3,3 0 1 1 7.99,1.5 Z", 0x5B7BB5), P("M2,15 C2,10.5 4.5,9 8,9 C11.5,9 14,10.5 14,15 Z", 0x5B7BB5)],
            ["import-csv"] = [doc, P("M5.5,7 H9 M5.5,9 H11 M5.5,11 H11", null, Gray), P("M0.5,6.5 H4 V4.5 L7.5,8 L4,11.5 V9.5 H0.5 Z", Green)],
            ["export-sql"] = [.. cylinder, P("M8.5,6.5 H12 V4.5 L15.5,8 L12,11.5 V9.5 H8.5 Z", Green)],
            ["export-grid"] = [.. table, P("M8.5,8.5 H12 V6.5 L15.5,10 L12,13.5 V11.5 H8.5 Z", Green)],
            ["export-settings"] = [doc, P("M8.5,8.5 H12 V6.5 L15.5,10 L12,13.5 V11.5 H8.5 Z", Green)],
            ["import-settings"] = [doc, P("M0.5,8.5 H4 V6.5 L7.5,10 L4,13.5 V11.5 H0.5 Z", Green)],
            ["help"] =
            [
                P("M8,0.5 A7.5,7.5 0 1 1 7.99,0.5 Z", Blue),
                P("M5.6,6 C5.6,4.4 6.6,3.4 8,3.4 C9.5,3.4 10.4,4.4 10.4,5.7 C10.4,7.5 8.4,7.6 8.4,9.4 V10", null, Paper, 1.6),
                P("M7.3,11.5 H9.1 V13.3 H7.3 Z", Paper),
            ],
            ["about"] = [P("M8,0.5 A7.5,7.5 0 1 1 7.99,0.5 Z", Blue), P("M7,6.5 H9 V12.5 H7 Z M7,3 H9 V5 H7 Z", Paper)],
            ["web"] = [P("M8,1 A7,7 0 1 1 7.99,1 Z", 0xDDEBFA, Blue), P("M1,8 H15 M8,1 C4.5,4.5 4.5,11.5 8,15 C11.5,11.5 11.5,4.5 8,1", null, Blue)],
            ["first"] = [P("M2,2 H4.5 V14 H2 Z M14,2 V14 L5,8 Z", Blue)],
            ["last"] = [P("M11.5,2 H14 V14 H11.5 Z M2,2 V14 L11,8 Z", Blue)],
            ["previous"] = [P("M10.5,2 V14 L3.5,8 Z", Blue)],
            ["next"] = [P("M5.5,2 V14 L12.5,8 Z", Blue)],
            ["next-rows"] = [P("M2,3 H14 L8,10 Z", Blue), P("M2,12 H14 V14 H2 Z", Blue)],
            ["all-rows"] = [P("M2,1 H14 L8,7 Z M2,8 H14 L8,14 Z", Blue)],
            ["insert-row"] = [P("M6.5,1.5 H9.5 V6.5 H14.5 V9.5 H9.5 V14.5 H6.5 V9.5 H1.5 V6.5 H6.5 Z", Green)],
            ["duplicate-row"] = [P("M1.5,3.5 H11.5 V7.5 H1.5 Z", Paper, Gray), P("M4.5,8.5 H14.5 V12.5 H4.5 Z", Paper, Gray), P("M10,9.5 H12 V11.5", null, Green, 1.5)],
            ["delete-row"] = [P("M1.5,6.5 H14.5 V9.5 H1.5 Z", Red)],
            ["post"] = [P("M1.5,8.5 L3.8,6.2 L6.8,9.2 L13.2,2.8 L15.5,5.1 L6.8,13.8 Z", Green)],
            ["cancel-edit"] = [P("M3,1.5 L8,6.5 L13,1.5 L14.5,3 L9.5,8 L14.5,13 L13,14.5 L8,9.5 L3,14.5 L1.5,13 L6.5,8 L1.5,3 Z", Red)],
            ["cancel-operation"] = [P("M3,3 H13 V13 H3 Z", Red, 0x8E1B1B)],
            ["run"] = [play],
            ["run-selection"] = [P("M2,1.5 V10.5 L9.5,6 Z", Green, 0x1E6B2A), P("M6,12.5 H15 M9,9.5 H15", null, Blue, 1.6)],
            ["run-current"] = [P("M5,1.5 V14.5 L15,8 Z", Green, 0x1E6B2A), P("M1.5,2 V14", null, Blue, 2)],
            ["run-file"] = [doc, P("M6,6.5 V13 L11.5,9.75 Z", Green)],
            ["diagram"] = [P("M1.5,1.5 H7.5 V6.5 H1.5 Z", Paper, Blue), P("M8.5,9.5 H14.5 V14.5 H8.5 Z", Paper, Blue), P("M7.5,4 H10.5 V9.5", null, Gray, 1.3)],
            ["open"] = [P("M1,3 H6 L7.5,4.5 H15 V14 H1 Z", 0xE8B84A, 0xA9802A)],
            ["save"] = floppy,
            ["save-as"] = [.. floppy, P("M10,15 L15,10", null, 0x404040, 1.6)],
            ["save-snippet"] = [.. floppy, P("M12.5,9 L13.4,11 L15.5,11.2 L13.9,12.6 L14.4,14.7 L12.5,13.6 L10.6,14.7 L11.1,12.6 L9.5,11.2 L11.6,11 Z", Yellow, DarkOrange, 0.6)],
            ["find"] = magnifier,
            ["find-again"] = [.. magnifier, P("M12,1 H15 V4", null, Green, 1.5)],
            ["find-server"] = [.. cylinder, P("M10,9 A2.8,2.8 0 1 1 9.99,9 Z", 0xDDEBFA, DarkBlue)],
            ["replace"] = [P("M5.5,1.5 A4,4 0 1 1 5.49,1.5 Z", 0xDDEBFA, DarkBlue, 1.3), P("M8.5,8.5 L11,11", null, Gray, 2), P("M10,14 H15 M13,12 L15,14 L13,16", null, Green, 1.3)],
            ["reformat"] = [P("M1,2.5 H15 M3,6 H13 M3,9.5 H11 M1,13 H15", null, DarkBlue, 1.5)],
            ["stop-on-errors"] = [P("M5,1 H11 L15,5 V11 L11,15 H5 L1,11 V5 Z", Red), P("M4,7 H12 V9 H4 Z", Paper)],
            ["blob-text"] = [doc, P("M5.5,6.5 H7.5 V11.5 M9.5,6.5 H11.5 V11.5 H9.5 Z", null, Purple, 1.2)],
            ["word-wrap"] = [P("M1,3 H15 M1,7 H12 A2,2 0 0 1 12,11 H8 M1,11 H5 M1,15 H9", null, DarkBlue, 1.4), P("M9.5,9 L7.5,11 L9.5,13", null, DarkBlue, 1.4)],
            ["code-folding"] = [P("M1.5,1.5 H8.5 V8.5 H1.5 Z", Paper, Gray), P("M3,5 H7", null, 0x404040, 1.2), P("M5,8.5 V13 H14", null, Gray)],
            ["delimiter"] = [P("M6.5,3 H9.5 V6 H6.5 Z", 0x404040), P("M6.5,9 H9.5 V12 L7,15 H6 L7,12 H6.5 Z", 0x404040)],
            ["new-tab"] = [doc, P("M9.5,9 H15.5 M12.5,6 V12", null, Green, 1.8)],
            ["close-tab"] = [P("M4,4 L12,12 M12,4 L4,12", null, Gray, 1.6)],
            ["clear"] = [doc, P("M6,7 L11,12 M11,7 L6,12", null, Red, 1.4)],
            ["rename"] = [P("M1.5,4.5 H14.5 V11.5 H1.5 Z", Paper, Gray), P("M4,6.5 H9 M4,9.5 H8 M11,3 V13", null, 0x404040)],
            ["comment"] = [P("M2,14 L7,2 M8,14 L13,2", null, Green, 1.6)],
            ["filter"] = [funnel],
            ["filter-remove"] = [funnel, redX],
            ["sort-reset"] = [P("M4,2 V14 M1.5,11.5 L4,14 L6.5,11.5 M10,14 V2 M7.5,4.5 L10,2 L12.5,4.5", null, DarkBlue, 1.4), redX],
            ["pin"] = [P("M8,9 V15", null, Gray, 1.4), P("M5.5,1.5 H10.5 L9.6,5.5 L12,9 H4 L6.4,5.5 Z", Red, 0x8E1C1C)],
            ["favorites"] = [P("M8,1 L10.1,5.6 L15,6.1 L11.2,9.5 L12.3,14.5 L8,11.9 L3.7,14.5 L4.8,9.5 L1,6.1 L5.9,5.6 Z", Yellow, DarkOrange, 0.8)],
            ["database"] = cylinder,
            ["table"] = table,
            ["table-copy"] = [.. table, P("M9.5,9.5 H15.5 V15.5 H9.5 Z", Paper, Green)],
            ["empty-table"] = [.. table, P("M8,8 L14,14 M14,8 L8,14", null, Red, 1.5)],
            ["view"] = [P("M1.5,2.5 H14.5 V13.5 H1.5 Z", Paper, 0x5B9A5B), P("M1.5,2.5 H14.5 V5.5 H1.5 Z", 0x5B9A5B), P("M3,9.5 C5,6.5 11,6.5 13,9.5 C11,12.5 5,12.5 3,9.5 Z", null, 0x5B9A5B)],
            ["procedure"] = [P("M5,2 C3,2 3.5,6 2,8 C3.5,10 3,14 5,14 M11,2 C13,2 12.5,6 14,8 C12.5,10 13,14 11,14", null, Purple, 1.5)],
            ["function"] = [P("M12,2.5 C9,1.5 8.5,3 8,6 L6.5,13.5 C6,15 4.5,14.5 3.5,14 M5,7 H11", null, Purple, 1.6)],
            ["trigger"] = [P("M9.5,1 L3,9 H8 L6.5,15 L13,7 H8 Z", Orange, DarkOrange)],
            ["event"] = [P("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z", 0xDDEBFA, Blue, 1.2), P("M8,4 V8 L11,10", null, DarkBlue, 1.4)],
            ["drop"] = [.. cylinder, redX],
            ["edit"] = [P("M2,14 L3,10 L11,2 L14,5 L6,13 Z", Orange, DarkOrange), P("M2,14 L3,10 L6,13 Z", 0x404040)],
            ["kill"] = [P("M3,1.5 L8,6.5 L13,1.5 L14.5,3 L9.5,8 L14.5,13 L13,14.5 L8,9.5 L3,14.5 L1.5,13 L6.5,8 L1.5,3 Z", Red)],
            ["delete"] = [P("M3,1.5 L8,6.5 L13,1.5 L14.5,3 L9.5,8 L14.5,13 L13,14.5 L8,9.5 L3,14.5 L1.5,13 L6.5,8 L1.5,3 Z", Red)],
            ["null"] = [P("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z M3.5,12.5 L12.5,3.5", null, 0x404040, 1.5)],
            ["properties"] = [P("M1.5,1.5 H14.5 V14.5 H1.5 Z", Paper, Gray), P("M3.5,4.5 H5 M7,4.5 H12.5 M3.5,8 H5 M7,8 H12.5 M3.5,11.5 H5 M7,11.5 H12.5", null, DarkBlue, 1.3)],
            ["preferences"] = [P(Gear(8, 8, 7, 5.2, 8), Gray, 0x404040, 0.5), P("M8,5.5 A2.5,2.5 0 1 1 7.99,5.5 Z", Paper)],
            ["select-all"] = [P("M1.5,1.5 H14.5 V14.5 H1.5 Z", 0xDDEBFA, Blue)],
            ["invert-selection"] = [P("M1.5,1.5 H14.5 V14.5 H1.5 Z", Paper, Blue), P("M1.5,1.5 H14.5 L1.5,14.5 Z", 0xDDEBFA)],
            ["image"] = [P("M1.5,2.5 H14.5 V13.5 H1.5 Z", Paper, Gray), P("M2,13 L6,8 L9,11 L11,9 L14,13 Z", Green), P("M11,4 A1.5,1.5 0 1 1 10.99,4 Z", Yellow)],
            ["insert-files"] = [doc, P("M5.5,13 V8 H11 L8.25,5 Z", Green)],
            ["follow-fk"] = [P("M4,8 A3,3 0 1 1 3.99,8 Z", null, Orange, 1.6), P("M7,8 H15 M12,8 V11 M14,8 V10.5", null, Orange, 1.6)],
            ["explain"] = [.. magnifier],
            ["maintenance"] = [P("M10,1.5 A4,4 0 0 0 6.5,7 L1.5,12 L4,14.5 L9,9.5 A4,4 0 0 0 14.5,6 L12,8 L9.5,6.5 L9.5,4 Z", Gray, 0x404040, 0.6)],
            ["bulk-edit"] = [.. table, P("M9,15 L10,12 L14,8 L16,10 L12,14 Z", Orange)],
            ["generate"] = [.. table, P("M12,8 L13,10.5 L15.5,11 L13.5,12.5 L14,15 L12,13.7 L10,15 L10.5,12.5 L8.5,11 L11,10.5 Z", Yellow, DarkOrange, 0.5)],
            ["console"] = [P("M1.5,2.5 H14.5 V13.5 H1.5 Z", 0x202020), P("M3.5,5 L6.5,8 L3.5,11 M8,11 H12", null, Paper, 1.3)],
            ["idle"] = [P("M8,4 A4,4 0 1 1 7.99,4 Z", Green)],
            ["busy"] = [P("M3.5,1.5 H12.5 M3.5,14.5 H12.5 M4.5,1.5 C4.5,6 8,6.5 8,8 C8,9.5 4.5,10 4.5,14.5 M11.5,1.5 C11.5,6 8,6.5 8,8 C8,9.5 11.5,10 11.5,14.5", null, DarkOrange, 1.3), P("M6,13.5 L8,11 L10,13.5 Z", Orange)],
            ["variables"] = [P("M1.5,2.5 H14.5 V13.5 H1.5 Z", Paper, Gray), P("M3.5,5.5 H6.5 M8.5,5.5 H12.5 M3.5,8 H6.5 M8.5,8 H12.5 M3.5,10.5 H6.5 M8.5,10.5 H12.5", null, DarkBlue, 1.2)],
            ["status"] = [P("M1,9 H4 L6,3 L9.5,13 L11.5,7 H15", null, Green, 1.6)],
            ["processes"] = [P("M2,2.5 H14 V5.5 H2 Z M2,6.5 H11 V9.5 H2 Z M2,10.5 H8 V13.5 H2 Z", Blue)],
            ["statistics"] = [P("M2,14.5 V8 H5 V14.5 Z M6.5,14.5 V3.5 H9.5 V14.5 Z M11,14.5 V10 H14 V14.5 Z", Purple), P("M1,15 H15", null, Gray, 1)],
            ["update"] = [P("M13,8 A5,5 0 1 1 11.6,4.4 L9.6,6.4 H15 V1 L13,3 A7,7 0 1 0 15,8 Z", Blue)],
        };
    }

    /// <summary>Roda dentada com <paramref name="teeth"/> dentes (contorno fechado).</summary>
    private static string Gear(double cx, double cy, double outer, double inner, int teeth)
    {
        var sb = new StringBuilder();
        var steps = teeth * 4;
        for (var i = 0; i < steps; i++)
        {
            var r = (i % 4) is 0 or 1 ? outer : inner;
            var a = (i * 2 * Math.PI / steps) - (Math.PI / 2);
            sb.Append(i == 0 ? 'M' : 'L')
              .Append((cx + (r * Math.Cos(a))).ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append((cy + (r * Math.Sin(a))).ToString("0.##", CultureInfo.InvariantCulture)).Append(' ');
        }
        return sb.Append('Z').ToString();
    }
}
