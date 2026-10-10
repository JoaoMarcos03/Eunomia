using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>Styles the editor needs (AvaloniaEdit template). Add to <c>Application.Styles</c>.</summary>
public sealed partial class SqlEditorStyles : Styles
{
    public SqlEditorStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
