using System.Globalization;

namespace Eunomia.Engine.Results.Tests;

/// <summary>
/// Lossless raw text (phase 14): what is edited, copied, duplicated, exported and put in a WHERE must come back to the same
/// value on the server, in any culture.
/// </summary>
public sealed class RawTextTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("d", "DOUBLE", TypeCategory.Real, StorageKind.Double, false, true),
        new ColumnInfo("f", "FLOAT", TypeCategory.Real, StorageKind.Double, false, true),
        new ColumnInfo("b", "BIT(8)", TypeCategory.Integer, StorageKind.Int64, false, true),
        new ColumnInfo("price", "DECIMAL(14,4)", TypeCategory.Real, StorageKind.Decimal, false, true, Scale: 4),
        new ColumnInfo("blob", "LONGBLOB", TypeCategory.Binary, StorageKind.Binary, false, true),
        new ColumnInfo("big", "BIGINT", TypeCategory.Integer, StorageKind.Int64, false, true),
        new ColumnInfo("bare", "BIT", TypeCategory.Integer, StorageKind.Int64, false, true),
    ]);

    private static readonly byte[] Blob = [.. Enumerable.Range(0, 1000).Select(i => (byte)(i * 7))];

    private static RowBlock Sample()
    {
        var b = new RowBlockBuilder(Schema, blockIndex: 0, firstRow: 0, rowCount: 2);
        b.SetDouble(0, 0, Math.PI);
        b.SetDouble(0, 1, 0.1f);
        b.SetInt64(0, 2, 10);
        b.SetDecimalUnscaled(0, 3, 12_345_678_9012);
        b.SetBytes(0, 4, Blob, truncated: false);
        b.SetInt64(0, 5, 1_234_567);
        b.SetInt64(0, 6, 5);
        b.SetDouble(1, 0, 0.0001);
        b.SetDouble(1, 1, -2.5e-8f);
        b.SetInt64(1, 2, 1);
        b.SetNull(1, 3);
        b.SetBytes(1, 4, [], truncated: false);
        b.SetInt64(1, 5, -1);
        b.SetInt64(1, 6, 0);
        return b.Build();
    }

    [Fact]
    public void RawLosesNoDigitsOrBytes()
    {
        var block = Sample();
        Assert.Equal("3.141592653589793", DisplayFormatter.FormatRaw(block, 0, 0));
        Assert.Equal(Math.PI, double.Parse(DisplayFormatter.FormatRaw(block, 0, 0)!, CultureInfo.InvariantCulture));
        Assert.Equal("0.1", DisplayFormatter.FormatRaw(block, 0, 1));
        Assert.Equal("0.0001", DisplayFormatter.FormatRaw(block, 1, 0));
        Assert.Equal(-2.5e-8f, float.Parse(DisplayFormatter.FormatRaw(block, 1, 1)!, CultureInfo.InvariantCulture));
        // BIT as binary digits with the column width: the DML reads "00001010" as b'00001010' (10), not as 2.
        Assert.Equal("00001010", DisplayFormatter.FormatRaw(block, 0, 2));
        Assert.Equal("101", DisplayFormatter.FormatRaw(block, 0, 6));
        Assert.Equal("12345678.9012", DisplayFormatter.FormatRaw(block, 0, 3));
        Assert.Null(DisplayFormatter.FormatRaw(block, 1, 3));
        // Full binary (the display shows only 256 bytes).
        Assert.Equal("0x" + Convert.ToHexString(Blob), DisplayFormatter.FormatRaw(block, 0, 4));
        Assert.Equal("0x", DisplayFormatter.FormatRaw(block, 1, 4));
        Assert.Equal("1234567", DisplayFormatter.FormatRaw(block, 0, 5));
    }

    [Fact]
    public void RawDoesNotDependOnCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-PT");
            Assert.Equal("3.141592653589793", DisplayFormatter.FormatRaw(Sample(), 0, 0));
            Assert.Equal("12345678.9012", DisplayFormatter.FormatRaw(Sample(), 0, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DisplayShowsEveryDoubleDigitAndBitsInBinary()
    {
        var block = Sample();
        var pt = new DisplayFormatter(CultureInfo.GetCultureInfo("pt-PT"));
        var invariant = new DisplayFormatter(CultureInfo.InvariantCulture);
        Assert.Equal("3,141592653589793", pt.Format(block, 0, 0));
        Assert.Equal("0.0001", invariant.Format(block, 1, 0));
        Assert.Equal("-2,5E-08", pt.Format(block, 1, 1));
        Assert.Equal("00001010", invariant.Format(block, 0, 2));
        Assert.StartsWith("0x", invariant.Format(block, 0, 4), StringComparison.Ordinal);
        Assert.EndsWith(DisplayFormatter.TruncatedSuffix, invariant.Format(block, 0, 4), StringComparison.Ordinal);

        var grouped = new RowBlockBuilder(Schema, 0, 0, 1);
        grouped.SetDouble(0, 0, -1234567.25);
        for (var c = 1; c < Schema.Count; c++)
        {
            grouped.SetNull(0, c);
        }
        var built = grouped.Build();
        Assert.Equal("-1,234,567.25", invariant.Format(built, 0, 0));
        Assert.Equal("-1234567,25", new DisplayFormatter(CultureInfo.GetCultureInfo("pt-PT"), thousandsSeparator: false).Format(built, 0, 0));
    }
}
