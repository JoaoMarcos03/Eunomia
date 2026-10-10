using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Eunomia.UI.Controls.Common.Brand;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Features.Sessions;
using Eunomia.UI.Features.Transfer;
using Eunomia.UI.Shell;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Marca (assets/brand): todas as janelas de topo têm o ícone do Eunomia.</summary>
public sealed class BrandTests
{
    [AvaloniaFact]
    public void IconeDasJanelasVemDosRecursos()
    {
        Assert.True(AssetLoader.Exists(BrandAssets.IconUri));
        Assert.Same(BrandAssets.WindowIcon, BrandAssets.WindowIcon);
    }

    [AvaloniaFact]
    public void LogoNoAboutENoStartDoSessionManager()
    {
        Assert.IsType<DrawingImage>(BrandAssets.Logo);
        Assert.Same(BrandAssets.Logo, BrandAssets.Logo);

        var about = new AboutDialog("0.13.1");
        about.Show();
        Assert.Same(BrandAssets.Logo, about.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "imgLogo").Source);
        about.Close();

        var manager = (SessionManagerWindow)AllWindows.Factories[typeof(SessionManagerWindow)]();
        manager.Show();
        var logo = manager.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "imgLogo");
        Assert.Same(BrandAssets.Logo, logo.Source);
        Assert.True(logo.Bounds.Width > 0);
        manager.Close();
    }

    [AvaloniaFact]
    public void TodasAsJanelasDeTopoTemOIconeDaMarca()
    {
        var assemblies = new[] { typeof(MainWindow), typeof(MessageDialog), typeof(SessionManagerWindow), typeof(SqlExportDialog) }
            .Select(t => t.Assembly);
        // Código de teste: corre sem trimming.
#pragma warning disable IL2026
        var types = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
#pragma warning restore IL2026
        Assert.Empty(types.Where(t => !AllWindows.Factories.ContainsKey(t)).Select(t => t.FullName));

        foreach (var (type, create) in AllWindows.Factories)
        {
            var window = create();
            Assert.True(ReferenceEquals(BrandAssets.WindowIcon, window.Icon), $"{type.Name} sem o ícone da marca");
            window.Close();
        }
    }
}
