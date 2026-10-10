namespace Eunomia.App.Desktop;

/// <summary>Marcas de tempo do arranque, gravadas pelo <c>--startup-probe</c> para decompor o PR-01.</summary>
internal static class StartupTrace
{
    private static readonly List<(string Name, DateTime Utc)> Marks = [];

    public static IReadOnlyList<(string Name, DateTime Utc)> All => Marks;

    public static void Mark(string name) => Marks.Add((name, DateTime.UtcNow));
}
