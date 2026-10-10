using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Eunomia.UI.Controls.Common.Brand;

/// <summary>
/// Eunomia brand (files in <c>assets/brand</c>, generated outside the repo): the window icon, the same
/// <c>eunomia.ico</c> that goes into the executable, and the vector logo. Every top-level window sets
/// <c>Icon = BrandAssets.WindowIcon</c> in its constructor.
/// </summary>
public static class BrandAssets
{
    public static readonly Uri IconUri = new("avares://Eunomia.UI.Controls.Common/Brand/eunomia.ico");

    private static WindowIcon? _windowIcon;
    private static BrandResources? _resources;

    /// <summary>Icon shared by every window (read once from the resources).</summary>
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

    /// <summary>Vector logo (symbol), from <c>EunomiaLogo.axaml</c>; shared by the controls that show it.</summary>
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
