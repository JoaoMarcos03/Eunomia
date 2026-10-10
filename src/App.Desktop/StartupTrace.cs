namespace Eunomia.App.Desktop;

/// <summary>Startup timestamps, written by <c>--startup-probe</c> to break down PR-01.</summary>
internal static class StartupTrace
{
    private static readonly List<(string Name, DateTime Utc)> Marks = [];

    public static IReadOnlyList<(string Name, DateTime Utc)> All => Marks;

    public static void Mark(string name) => Marks.Add((name, DateTime.UtcNow));
}
