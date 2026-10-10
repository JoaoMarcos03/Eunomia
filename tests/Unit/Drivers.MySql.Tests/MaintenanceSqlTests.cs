using Eunomia.Drivers.Abstractions;

namespace Eunomia.Drivers.MySql.Tests;

/// <summary>Maintenance statements (appendix B §2.2): the options each operation accepts, in HeidiSQL's order.</summary>
public sealed class MaintenanceSqlTests
{
    private static readonly MySqlDialect Dialect = new(MySqlConnectionFactory.ParseIdentity("10.4.32-MariaDB", null));
    private static readonly DbObjectRef People = new("shop", "shop", "people", DbObjectKind.Table);
    private const MaintenanceOptions All = MaintenanceOptions.Quick | MaintenanceOptions.Fast | MaintenanceOptions.Medium | MaintenanceOptions.Extended
                                           | MaintenanceOptions.Changed | MaintenanceOptions.UseFrm | MaintenanceOptions.ForUpgrade;

    [Theory]
    [InlineData(MaintenanceOperation.Check, All, "CHECK TABLE `shop`.`people` QUICK FAST MEDIUM EXTENDED CHANGED FOR UPGRADE")]
    [InlineData(MaintenanceOperation.Check, MaintenanceOptions.None, "CHECK TABLE `shop`.`people`")]
    [InlineData(MaintenanceOperation.Analyze, All, "ANALYZE TABLE `shop`.`people`")]
    [InlineData(MaintenanceOperation.Checksum, MaintenanceOptions.Quick, "CHECKSUM TABLE `shop`.`people` QUICK")]
    [InlineData(MaintenanceOperation.Checksum, All, "CHECKSUM TABLE `shop`.`people` EXTENDED")]
    [InlineData(MaintenanceOperation.Optimize, All, "OPTIMIZE TABLE `shop`.`people`")]
    [InlineData(MaintenanceOperation.Repair, All, "REPAIR TABLE `shop`.`people` QUICK EXTENDED USE_FRM")]
    public void OnlyTheOptionsTheOperationAccepts(MaintenanceOperation operation, MaintenanceOptions options, string expected)
    {
        Assert.Equal(expected, Dialect.Maintenance(operation, People, options));
    }

    [Fact]
    public void NamesWithBackticksAreQuoted()
    {
        var odd = new DbObjectRef("my`db", "my`db", "a b", DbObjectKind.View);

        Assert.Equal("CHECK TABLE `my``db`.`a b`", Dialect.Maintenance(MaintenanceOperation.Check, odd, MaintenanceOptions.None));
    }
}
