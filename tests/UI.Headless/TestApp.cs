using Avalonia;
using Avalonia.Headless;
using Eunomia.UI.Shell;

[assembly: AvaloniaTestApplication(typeof(Eunomia.UI.Headless.Tests.TestApp))]

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Headless application with real Skia rendering (not the stub drawing), so the snapshots have pixels.
/// </summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>The application's own look (<see cref="ShellTheme"/>), so the snapshots show what the user sees.</summary>
    public override void Initialize() => ShellTheme.Install(this);
}
