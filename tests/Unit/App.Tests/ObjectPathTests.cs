using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Tests;

/// <summary>Caminho dos favoritos no formato do HeidiSQL (FAM-T03).</summary>
public sealed class ObjectPathTests
{
    [Fact]
    public void MySqlTemSchemaVazio()
    {
        Assert.Equal("loja||clientes", ObjectPath.Of(new DbObjectRef("loja", "loja", "clientes", DbObjectKind.Table)));
    }

    [Fact]
    public void SchemaDiferenteDoCatalogFicaNoMeio()
    {
        Assert.Equal("app|public|clientes", ObjectPath.Of(new DbObjectRef("app", "public", "clientes", DbObjectKind.View)));
    }
}
