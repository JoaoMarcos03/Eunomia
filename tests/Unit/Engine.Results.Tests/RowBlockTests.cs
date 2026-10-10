using System.Globalization;

namespace Eunomia.Engine.Results.Tests;

public sealed class RowBlockTests
{
    private static readonly ResultSchema Schema = new(
    [
        new ColumnInfo("id", "INT", TypeCategory.Integer, StorageKind.Int64, IsPrimaryKey: true, IsNullable: false),
        new ColumnInfo("price", "DECIMAL(10,2)", TypeCategory.Real, StorageKind.Decimal, false, true, Scale: 2),
        new ColumnInfo("ratio", "DOUBLE", TypeCategory.Real, StorageKind.Double, false, true),
        new ColumnInfo("created", "DATETIME", TypeCategory.Temporal, StorageKind.DateTime, false, true),
        new ColumnInfo("day", "DATE", TypeCategory.Temporal, StorageKind.DateTime, false, true),
        new ColumnInfo("name", "VARCHAR(20)", TypeCategory.Text, StorageKind.String, false, true),
    ]);

    private static RowBlock Sample()
    {
        var b = new RowBlockBuilder(Schema, blockIndex: 0, firstRow: 0, rowCount: 3);
        b.SetInt64(0, 0, 1234567);
        b.SetDecimalUnscaled(0, 1, 123456);
        b.SetDouble(0, 2, 1.5);
        b.SetDateTime(0, 3, new DateTime(2026, 10, 1, 13, 5, 9, DateTimeKind.Unspecified));
        b.SetDateTime(0, 4, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified));
        b.SetString(0, 5, "ação");

        b.SetInt64(1, 0, -5);
        b.SetNull(1, 1);
        b.SetNull(1, 2);
        b.SetNull(1, 3);
        b.SetNull(1, 4);
        b.SetNull(1, 5);

        b.SetInt64(2, 0, 0);
        b.SetDecimalUnscaled(2, 1, -7);
        // Row 2 of the text column is not written: it stays empty.
        return b.Build();
    }

    [Fact]
    public void StoresAndReturnsValuesPerType()
    {
        var block = Sample();

        Assert.Equal(1234567, block.GetInt64(0, 0));
        Assert.Equal(123456, block.GetDecimalUnscaled(0, 1));
        Assert.Equal(1.5, block.GetDouble(0, 2));
        Assert.Equal(new DateTime(2026, 10, 1, 13, 5, 9, DateTimeKind.Unspecified), block.GetDateTime(0, 3));
        Assert.Equal("ação", block.GetString(0, 5));
        Assert.Equal(6, block.GetUtf8(0, 5).Length);
        Assert.True(block.IsNull(1, 5));
        Assert.Equal("", block.GetString(2, 5));
        Assert.False(block.IsNull(2, 5));
    }

    [Fact]
    public void OutOfOrderTextIsRejected()
    {
        var b = new RowBlockBuilder(Schema, 0, 0, 3);
        b.SetString(2, 5, "c");

        Assert.Throws<InvalidOperationException>(() => b.SetString(1, 5, "b"));
    }

    [Fact]
    public void ByteSizeCountsTheArrays()
    {
        var block = Sample();

        // 5 columns of 3 values of 8 bytes + text (6 UTF-8 bytes + 4 offsets of 4 bytes) + bitmaps.
        Assert.Equal((5 * 3 * 8) + 6 + (4 * 4) + (6 * 2 * 8), block.ByteSize);
    }

    [Fact]
    public void FormatterShowsNullThousandsSeparatorAndDecimals()
    {
        var block = Sample();
        var f = new DisplayFormatter(CultureInfo.InvariantCulture);

        Assert.Equal("1,234,567", f.Format(block, 0, 0));
        Assert.Equal("1,234.56", f.Format(block, 0, 1));
        Assert.Equal("1.5", f.Format(block, 0, 2));
        Assert.Equal("2026-10-01 13:05:09", f.Format(block, 0, 3));
        Assert.Equal("2026-10-01", f.Format(block, 0, 4));
        Assert.Equal("ação", f.Format(block, 0, 5));
        Assert.Equal("-5", f.Format(block, 1, 0));
        Assert.Equal(DisplayFormatter.NullText, f.Format(block, 1, 1));
        Assert.Equal("(NULL)", f.Format(block, 1, 5));
        Assert.Equal("-0.07", f.Format(block, 2, 1));
    }

    [Fact]
    public void FormatterWithoutThousandsSeparator()
    {
        var f = new DisplayFormatter(CultureInfo.InvariantCulture, thousandsSeparator: false);

        Assert.Equal("1234567", f.Format(Sample(), 0, 0));
        Assert.Equal("1234.56", f.Format(Sample(), 0, 1));
    }

    [Fact]
    public void FormatterUsesTheCultureSeparators()
    {
        var f = new DisplayFormatter(CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("1.234.567", f.Format(Sample(), 0, 0));
        Assert.Equal("1.234,56", f.Format(Sample(), 0, 1));
    }

    [Fact]
    public void CutTextGetsASuffix()
    {
        var schema = new ResultSchema([new ColumnInfo("t", "TEXT", TypeCategory.Text, StorageKind.String, false, true)]);
        var b = new RowBlockBuilder(schema, 0, 0, 2);
        b.SetString(0, 0, "abc", truncated: true);
        b.SetString(1, 0, "abc");
        var block = b.Build();
        var f = new DisplayFormatter(CultureInfo.InvariantCulture);

        Assert.Equal("abc [...]", f.Format(block, 0, 0));
        Assert.Equal("abc", f.Format(block, 1, 0));
    }
}
