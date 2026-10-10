using Eunomia.App.Data;
using Eunomia.Drivers.Abstractions;
using Eunomia.Drivers.MySql;

namespace Eunomia.App.Tests;

/// <summary>SQL dos filtros do Data tab (anexo D B9, B10; FAM-G12, G13).</summary>
public sealed class DataFiltersTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));

    private static ColumnDefinition Col(string name, string type, LogicalTypeKind kind) =>
        new(name, type, new LogicalType(kind), true, null, false, false, false, null);

    private static readonly ColumnDefinition Id = Col("id", "int(11)", LogicalTypeKind.Integer);
    private static readonly ColumnDefinition Name = Col("name", "varchar(20)", LogicalTypeKind.Text);
    private static readonly ColumnDefinition Created = Col("created", "datetime", LogicalTypeKind.DateTime);
    private static readonly ColumnDefinition Flags = Col("flags", "bit(3)", LogicalTypeKind.Bit);
    private static readonly ColumnDefinition Data = Col("data", "blob", LogicalTypeKind.Binary);

    [Fact]
    public void MultiColumnLikeEmTodasAsColunas()
    {
        Assert.Equal(
            "`id` LIKE '%a''b%' OR `name` LIKE '%a''b%' OR CAST(`created` AS CHAR) LIKE '%a''b%'",
            DataFilters.MultiColumn(Dialect, [Id, Name, Created], "a'b"));
        Assert.Equal(string.Empty, DataFilters.MultiColumn(Dialect, [Id], string.Empty));
        var many = DataFilters.MultiColumn(Dialect, Enumerable.Range(0, 12).Select(i => Col("coluna_" + i, "text", LogicalTypeKind.Text)), "x");
        Assert.Contains(" OR\n", many, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapingPorTipo()
    {
        Assert.Equal("42", DataFilters.Escape(Dialect, Id, "42"));
        Assert.Equal("'4x'", DataFilters.Escape(Dialect, Id, "4x"));
        Assert.Equal("b'101'", DataFilters.Escape(Dialect, Flags, "101"));
        Assert.Equal("0x0A0B", DataFilters.Escape(Dialect, Data, "0x0A0B"));
        // Barras em hex com introdutor: o mesmo valor em qualquer sql_mode.
        Assert.Equal("_utf8mb4 X'615C62'", DataFilters.Escape(Dialect, Name, @"a\b"));
    }

    [Fact]
    public void ValoresDasLinhasSelecionadas()
    {
        string?[] values = ["a", "b", "a", null];

        Assert.Equal("`name` IN ('a', 'b') OR `name` IS NULL", DataFilters.FromValues(Dialect, Name, values, QuickFilterOperator.Equal));
        Assert.Equal("`name` NOT IN ('a', 'b') AND `name` IS NOT NULL", DataFilters.FromValues(Dialect, Name, values, QuickFilterOperator.NotEqual));
        Assert.Equal("`name` LIKE 'a%' ESCAPE '!' OR `name` LIKE 'b%' ESCAPE '!'", DataFilters.FromValues(Dialect, Name, values, QuickFilterOperator.StartsWith));
        Assert.Equal("`name` LIKE '%a%' ESCAPE '!' OR `name` LIKE '%b%' ESCAPE '!'", DataFilters.FromValues(Dialect, Name, values, QuickFilterOperator.Contains));
        Assert.Equal("`id` > 5", DataFilters.FromValues(Dialect, Id, ["5"], QuickFilterOperator.Greater));
        Assert.Equal("`name` IS NULL", DataFilters.FromValues(Dialect, Name, [null], QuickFilterOperator.Equal));
        Assert.Null(DataFilters.FromValues(Dialect, Name, [null], QuickFilterOperator.Contains));
    }

    [Fact]
    public void ValoresParamAo1Kb()
    {
        var values = Enumerable.Range(0, 100).Select(i => i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 47)).ToList();

        var sql = DataFilters.FromValues(Dialect, Name, values, QuickFilterOperator.Equal)!;

        Assert.Equal(20, sql.Split(", ").Length);
    }

    [Fact]
    public void ValorEscritoOuDoClipboard()
    {
        Assert.Equal("`id` = 7", DataFilters.Single(Dialect, Id, QuickFilterOperator.Equal, "7"));
        Assert.Equal("`name` != 'x'", DataFilters.Single(Dialect, Name, QuickFilterOperator.NotEqual, "x"));
        Assert.Equal("`name` LIKE '%x%' ESCAPE '!'", DataFilters.Single(Dialect, Name, QuickFilterOperator.Contains, "x"));
        Assert.Equal("`id` IN (1, 2, 3)", DataFilters.Single(Dialect, Id, QuickFilterOperator.InVerbatim, "1, 2, 3"));
        // O clipboard nunca sai do IN: cada item é um literal.
        Assert.Equal("`id` IN ('0); DELETE FROM t; --')", DataFilters.Single(Dialect, Id, QuickFilterOperator.InVerbatim, "0); DELETE FROM t; -- "));
        Assert.Equal("`name` IN ('a', 'b,c', 'd''e', '3')", DataFilters.Single(Dialect, Name, QuickFilterOperator.InVerbatim, "a\n'b,c'\t\"d'e\", 3"));
        // Curingas do valor escapados: um "_" ou "%" de uma célula conta letra a letra.
        Assert.Equal("`name` LIKE 'a!_b!%c!!%' ESCAPE '!'", DataFilters.Single(Dialect, Name, QuickFilterOperator.StartsWith, "a_b%c!"));
        Assert.Equal("`id` = 'NaN'", DataFilters.Single(Dialect, Id, QuickFilterOperator.Equal, "NaN"));
        Assert.Equal("`id` IS NOT NULL", DataFilters.Single(Dialect, Id, QuickFilterOperator.IsNotNull, string.Empty));
    }

    [Fact]
    public void ShiftJuntaComAnd()
    {
        Assert.Equal("`id` = 1", DataFilters.Combine(null, "`id` = 1"));
        Assert.Equal("`id` = 1\nAND `name` = 'a'", DataFilters.Combine("`id` = 1", "`name` = 'a'"));
        Assert.Equal("(`id` = 1 OR `id` = 2)\nAND `name` = 'a'", DataFilters.Combine("`id` = 1 OR `id` = 2", "`name` = 'a'"));
        Assert.Equal("`id` = 1 AND x", DataFilters.Combine("`id` = 1 AND x", "`id` = 1"));
    }

    [Fact]
    public void CaptionCortadaA100()
    {
        Assert.Equal("`a` = 1 OR `b` = 2", DataFilters.Caption("`a` = 1\nOR `b` = 2"));
        Assert.Equal(100, DataFilters.Caption(new string('x', 300)).Length);
    }
}
