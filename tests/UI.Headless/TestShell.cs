using Eunomia.App.Settings;
using Eunomia.UI.Shell;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Shell with in-memory settings for the main window tests.</summary>
internal static class TestShell
{
    public static ShellContext CreateContext(AppSettings? settings = null) => CreateContext(settings ?? new AppSettings(), out _);

    public static ShellContext CreateContext(AppSettings settings, out MemorySettingsStore store)
    {
        settings.MainWindow.Width = 1280;
        settings.MainWindow.Height = 800;
        store = new MemorySettingsStore(settings);
        // A fixed computer, so the preferences read the same everywhere; with 16 GB no default limit is lowered.
        return new ShellContext(settings, store) { Machine = new MachineProfile(8, 16L * 1024 * 1024 * 1024) };
    }

    internal sealed class MemorySettingsStore(AppSettings settings) : ISettingsStore
    {
        public int Saves { get; private set; }

        public AppSettings Saved { get; private set; } = settings;

        public AppSettings Load() => Saved;

        public void Save(AppSettings value)
        {
            Saves++;
            Saved = value;
        }
    }
}
