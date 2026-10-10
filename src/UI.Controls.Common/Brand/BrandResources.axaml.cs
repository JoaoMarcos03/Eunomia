using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Eunomia.UI.Controls.Common.Brand;

/// <summary>Brand resources: <c>EunomiaLogo</c> (<c>DrawingImage</c>, viewBox 256).</summary>
public sealed partial class BrandResources : ResourceDictionary
{
    public const string LogoKey = "EunomiaLogo";

    public BrandResources()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
