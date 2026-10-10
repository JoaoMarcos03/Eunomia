namespace Eunomia.Engine.Results.Synthetic;

/// <summary>Pure functions that generate each value from a hash of (seed, row, column).</summary>
internal static class SyntheticValues
{
    public const int MaxTextLength = 1024;

    private static readonly DateTime DateBase = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private const long DateRangeSeconds = 26L * 365 * 24 * 3600;

    // Generic words, with a few accents to exercise UTF-8.
    private static readonly string[] Words =
    [
        "alpha", "beta", "gamma", "delta", "order", "stock", "price", "client", "store", "product",
        "discount", "budget", "status", "role", "account", "invoice", "line", "item", "value", "total",
        "north", "south", "east", "west", "red", "green", "blue", "black", "white", "large",
        "small", "medium", "premium", "basic", "standard", "express", "ação", "preço", "coração", "café",
        "Größe", "número", "lisboa", "porto", "braga", "faro", "coimbra", "aveiro", "setúbal", "évora",
        "server", "table", "index", "query", "filter", "export", "import", "session", "backup", "report",
        "2026", "v2", "id", "x",
    ];

    public static ulong Hash(ulong seed, long row, int column)
    {
        // SplitMix64 over the combination: stable across .NET versions (does not use HashCode).
        var z = seed ^ ((ulong)row * 0x9E3779B97F4A7C15UL) ^ ((ulong)(column + 1) * 0xC2B2AE3D27D4EB4FUL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>About 8% of the values in nullable columns are NULL.</summary>
    public static bool IsNull(ulong h) => (h & 0xFF) < 20;

    public static long Int(ulong h) => (long)((h >> 8) % 20_000_000) - 10_000_000;

    public static long BigInt(ulong h) => (long)((h >> 8) % 1_000_000_000_000UL);

    public static long DecimalUnscaled(ulong h) => (long)((h >> 8) % 1_000_000_000UL);

    public static double Double(ulong h) => ((long)((h >> 8) % 20_000_000) - 10_000_000) / 1000.0;

    public static DateTime DateTime(ulong h) => DateBase.AddSeconds((long)((h >> 8) % (ulong)DateRangeSeconds));

    public static string Prefix(SyntheticKind kind) => kind switch
    {
        SyntheticKind.Int => "int",
        SyntheticKind.BigInt => "bigint",
        SyntheticKind.Decimal => "decimal",
        SyntheticKind.Double => "double",
        SyntheticKind.DateTime => "datetime",
        SyntheticKind.Date => "date",
        SyntheticKind.VarcharShort => "name",
        SyntheticKind.VarcharLong => "description",
        _ => "notes",
    };

    /// <summary>
    /// Writes text into <paramref name="buffer"/> and returns the length written;
    /// <paramref name="fullLength"/> is the length the text would have without the buffer limit.
    /// </summary>
    public static int Text(ulong h, SyntheticKind kind, Span<char> buffer, out int fullLength)
    {
        var (minWords, maxWords, maxChars) = kind switch
        {
            SyntheticKind.VarcharShort => (1, 4, 30),
            SyntheticKind.VarcharLong => (4, 40, 255),
            _ => (10, 150, MaxTextLength),
        };
        var state = h;
        var words = minWords + (int)((state >> 12) % (ulong)(maxWords - minWords + 1));
        var length = 0;
        fullLength = 0;
        for (var w = 0; w < words; w++)
        {
            state = Next(state);
            var word = Words[(int)(state % (ulong)Words.Length)];
            var needed = (length == 0 ? 0 : 1) + word.Length;
            if (length + needed > maxChars)
            {
                break;
            }
            if (length > 0)
            {
                buffer[length++] = ' ';
            }
            word.AsSpan().CopyTo(buffer[length..]);
            length += word.Length;
        }
        fullLength = length;
        return length;
    }

    private static ulong Next(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
