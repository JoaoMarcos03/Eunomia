namespace Eunomia.Engine.Sql;

/// <summary>Known SQL languages (<see cref="ISqlLanguage"/>).</summary>
public static class SqlLanguages
{
    /// <summary>MySQL and MariaDB.</summary>
    public static ISqlLanguage MySql { get; } = new MySqlLanguage();

    /// <summary>Language without a connected session (SQL log, editor without a session): MySQL's, the only supported family.</summary>
    public static ISqlLanguage Default => MySql;
}
