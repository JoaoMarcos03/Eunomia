using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Eunomia.UI.Controls.SqlEditor;

/// <summary>Estilos de que o editor precisa (template do AvaloniaEdit). Adicionar a <c>Application.Styles</c>.</summary>
public sealed partial class SqlEditorStyles : Styles
{
    public SqlEditorStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
