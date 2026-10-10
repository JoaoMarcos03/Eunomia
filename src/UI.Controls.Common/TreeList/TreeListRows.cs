using System.Globalization;
using Eunomia.UI.Controls.Common.Formatting;

namespace Eunomia.UI.Controls.Common.TreeList;

/// <summary>How a cell is shown in the Host and Database tab lists.</summary>
public enum CellFormat
{
    Text,

    /// <summary>Number with a thousands separator (<c>FormatNumber</c>).</summary>
    Number,

    /// <summary>Bytes as in the tree ("192.0 KiB").</summary>
    Size,

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    DateTime,

    /// <summary>Number with one decimal place (Host tab averages).</summary>
    Decimal,

    /// <summary>Percentage with one decimal place ("12.5 %").</summary>
    Percent,
}

/// <summary>List rows from raw values: formatted text, sort key and proportional bars.</summary>
public static class TreeListRows
{
    public static string Format(object? value, CellFormat format, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return (value, format) switch
        {
            (null, _) => string.Empty,
            (long n, CellFormat.Size) => SizeFormatter.Format(n, culture),
            (long n, CellFormat.Number) => n.ToString("N0", culture),
            (int n, CellFormat.Number) => n.ToString("N0", culture),
            (double d, CellFormat.Number) => d.ToString("N0", culture),
            (double d, CellFormat.Decimal) => d.ToString("N1", culture),
            (double d, CellFormat.Percent) => d.ToString("N1", culture) + " %",
            (System.DateTime t, _) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            (TimeSpan s, _) => ((long)s.TotalSeconds).ToString("N0", culture),
            _ => Convert.ToString(value, culture) ?? string.Empty,
        };
    }

    /// <summary>Node with the formatted cells and the numeric values and dates as sort keys.</summary>
    public static TreeListNode Create(IReadOnlyList<object?> values, IReadOnlyList<CellFormat> formats, object? tag = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(formats);
        var cells = new string[values.Count];
        Dictionary<int, IComparable>? keys = null;
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            cells[i] = Format(value, i < formats.Count ? formats[i] : CellFormat.Text, culture);
            if (value is long or int or double or System.DateTime or TimeSpan)
            {
                keys ??= [];
                keys[i] = value is int n ? (long)n : value is TimeSpan s ? s.TotalSeconds : (IComparable)value;
            }
        }
        return new TreeListNode(cells) { Tag = tag, SortKeys = keys };
    }

    /// <summary>Bars proportional to the column's largest value (the Host tab lists and the Size column).</summary>
    public static void ApplyBars(IReadOnlyList<TreeListNode> nodes, IEnumerable<int> columns)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(columns);
        foreach (var column in columns)
        {
            double max = 0;
            foreach (var node in nodes)
            {
                max = Math.Max(max, Numeric(node, column));
            }
            if (max <= 0)
            {
                continue;
            }
            foreach (var node in nodes)
            {
                var value = Numeric(node, column);
                if (value > 0)
                {
                    node.Bars ??= [];
                    node.Bars[column] = value / max;
                }
            }
        }
    }

    private static double Numeric(TreeListNode node, int column) =>
        node.SortKeys?.GetValueOrDefault(column) switch
        {
            long n => n,
            double d => d,
            _ => 0,
        };
}
