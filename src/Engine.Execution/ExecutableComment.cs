namespace Eunomia.Engine.Execution;

/// <summary>Executable comments: <c>/*! … */</c> (MySQL and MariaDB) and <c>/*M! … */</c> (MariaDB only) run their content.</summary>
internal static class ExecutableComment
{
    /// <summary>Length of the opening marker at <paramref name="i"/> (3 for <c>/*!</c>, 4 for <c>/*M!</c>), or 0 if there is none.</summary>
    public static int MarkerLength(string text, int i)
    {
        if (i + 2 >= text.Length || text[i] != '/' || text[i + 1] != '*')
        {
            return 0;
        }
        if (text[i + 2] == '!')
        {
            return 3;
        }
        return i + 3 < text.Length && text[i + 2] is 'M' or 'm' && text[i + 3] == '!' ? 4 : 0;
    }
}
