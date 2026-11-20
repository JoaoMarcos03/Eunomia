using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql.Tests;

/// <summary>Only ASCII digits go unquoted: <c>\d</c> also matched Arabic-Indic and full-width digits, which the server rejects.</summary>
public sealed class DmlNumberDigitsTests
{
    private static readonly MySqlDmlComposer Dml = new(new MySqlDialect(new ServerIdentity(DbEngine.MariaDb, new Version(10, 4, 32), "10.4.32-MariaDB", null)));

    [Theory]
    [InlineData("٣")]
    [InlineData("３")]
    [InlineData("1٣.5")]
    public void UnicodeDigitsAreQuoted(string text)
    {
        var column = new ColumnDefinition("c", "INT", new LogicalType(LogicalTypeKind.Integer), true, null, false, false, false, null);

        Assert.StartsWith("'", Dml.Literal(column, new CellLiteral(text)), StringComparison.Ordinal);
        Assert.Equal("42", Dml.Literal(column, new CellLiteral("42")));
    }
}
