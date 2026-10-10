using Avalonia;
using Avalonia.Headless;
using Eunomia.UI.Shell;

[assembly: AvaloniaTestApplication(typeof(Eunomia.UI.Headless.Tests.TestApp))]

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Aplicação headless com rendering Skia real (não o desenho stub), para os snapshots terem pixels.
/// </summary>
public sealed class TestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>O mesmo aspeto da aplicação (<see cref="ShellTheme"/>), para os snapshots mostrarem o que o utilizador vê.</summary>
    public override void Initialize() => ShellTheme.Install(this);
}
