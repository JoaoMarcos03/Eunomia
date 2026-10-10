using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Eunomia.UI.Controls.Common.Brand;

/// <summary>
/// Marca do Eunomia (ficheiros em <c>assets/brand</c>, gerados fora do repo): o ícone das janelas, o mesmo
/// <c>eunomia.ico</c> que vai no executável, e o logo vetorial. Cada janela de topo põe
/// <c>Icon = BrandAssets.WindowIcon</c> no construtor.
/// </summary>
public static class BrandAssets
{
    public static readonly Uri IconUri = new("avares://Eunomia.UI.Controls.Common/Brand/eunomia.ico");

    private static WindowIcon? _windowIcon;
    private static BrandResources? _resources;

    /// <summary>Ícone partilhado por todas as janelas (lido uma vez dos recursos).</summary>
    public static WindowIcon WindowIcon
    {
        get
        {
            if (_windowIcon is null)
            {
                using var stream = AssetLoader.Open(IconUri);
                _windowIcon = new WindowIcon(stream);
            }
            return _windowIcon;
        }
    }

    /// <summary>Logo (símbolo) vetorial, de <c>EunomiaLogo.axaml</c>; partilhado pelos controlos que o mostram.</summary>
    public static IImage Logo
    {
        get
        {
            _resources ??= new BrandResources();
            return _resources.TryGetResource(BrandResources.LogoKey, null, out var logo) && logo is IImage image
                ? image
                : throw new InvalidOperationException("Recurso EunomiaLogo em falta.");
        }
    }
}
