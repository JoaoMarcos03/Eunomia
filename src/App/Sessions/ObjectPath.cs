using Eunomia.Drivers.Abstractions;

namespace Eunomia.App.Sessions;

/// <summary>
/// Identity of an object in the favourites and in the already selected objects: <c>db|schema|name</c>, like HeidiSQL's
/// <c>TDBObject.Path</c> (appendix D A5), so imported favourites keep matching. On MySQL and
/// MariaDB the schema is empty (<c>db||name</c>).
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
