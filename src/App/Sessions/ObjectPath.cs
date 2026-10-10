using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Sessions;

/// <summary>
/// Identidade de um objeto nos favoritos e nos objetos já selecionados: <c>db|schema|nome</c>, como o
/// <c>TDBObject.Path</c> do HeidiSQL (anexo D A5), para os favoritos importados continuarem a bater certo. Em MySQL e
/// MariaDB o schema é vazio (<c>db||nome</c>).
/// </summary>
public static class ObjectPath
{
    public const char Delimiter = '|';

    public static string Of(DbObjectRef obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var schema = string.Equals(obj.Catalog, obj.Schema, StringComparison.Ordinal) ? string.Empty : obj.Schema;
        return string.Concat(obj.Catalog, "|", schema, "|", obj.Name);
    }
}
