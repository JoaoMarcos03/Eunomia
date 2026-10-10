using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Tests;

/// <summary>The grid's Insert value ▸ (appendix D B16; FAM-G10).</summary>
public sealed class InsertValueItemsTests
{
    [Fact]
    public void DatasDoServidorUtcUnixEGuids()
    {
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var values = InsertValueItems.Compute(new DateTime(2026, 10, 6, 18, 30, 5), new DateTime(2026, 10, 6, 19, 0, 0), new DateTime(2026, 10, 6, 18, 0, 0), guid);

        Assert.Equal("2026-10-06 18:30:05", values["insertValue.datetime"]);
        Assert.Equal("2026-10-06", values["insertValue.date"]);
        Assert.Equal("18:30:05", values["insertValue.time"]);
        Assert.Equal("2026", values["insertValue.year"]);
        Assert.Equal("1791313200", values["insertValue.unixTimestamp"]);
        Assert.Equal("1791309600", values["insertValue.utcUnixTimestamp"]);
        Assert.Equal("2026-10-06 18:00:00", values["insertValue.utcDatetime"]);
        Assert.Equal("{0F8FAD5B-D9CB-469F-A165-70867728950E}", values["insertValue.guid"]);
        Assert.Equal("0F8FAD5B-D9CB-469F-A165-70867728950E", values["insertValue.guidNoBraces"]);
        Assert.Equal("{0f8fad5b-d9cb-469f-a165-70867728950e}", values["insertValue.guidLower"]);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", values["insertValue.guidLowerNoBraces"]);
    }

    [Theory]
    [InlineData("'abc'", "abc")]
    [InlineData("'it''s'", "it's")]
    [InlineData("0", "0")]
    [InlineData("-1.5", "-1.5")]
    [InlineData("CURRENT_TIMESTAMP", null)]
    [InlineData("NULL", null)]
    [InlineData(null, null)]
    public void DefaultSoQuandoELiteral(string? expression, string? expected)
    {
        var column = new ColumnDefinition("c", "VARCHAR(10)", new LogicalType(LogicalTypeKind.Text), true, expression, false, false, false, null);
        Assert.Equal(expected, InsertValueItems.DefaultLiteral(column));
    }
}
