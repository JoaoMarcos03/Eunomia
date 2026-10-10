using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Tests;

/// <summary>Favourites path in HeidiSQL's format (FAM-T03).</summary>
public sealed class ObjectPathTests
{
    [Fact]
    public void MySqlHasAnEmptySchema()
    {
        Assert.Equal("shop||customers", ObjectPath.Of(new DbObjectRef("shop", "shop", "customers", DbObjectKind.Table)));
    }

    [Fact]
    public void SchemaDifferentFromTheCatalogGoesInTheMiddle()
    {
        Assert.Equal("app|public|customers", ObjectPath.Of(new DbObjectRef("app", "public", "customers", DbObjectKind.View)));
    }
}
