namespace Eunomia.Engine.Sql;

/// <summary>Linguagens SQL conhecidas (<see cref="ISqlLanguage"/>).</summary>
public static class SqlLanguages
{
    /// <summary>MySQL e MariaDB.</summary>
    public static ISqlLanguage MySql { get; } = new MySqlLanguage();

    /// <summary>Linguagem sem sessão ligada (SQL log, editor sem sessão): a do MySQL, a única família suportada.</summary>
    public static ISqlLanguage Default => MySql;
}
