using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Tree;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Os nomes da árvore usam a largura toda quando a linha não tem tamanho para mostrar (Fase 13).</summary>
public sealed class TreeNameWidthTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task NomeSoCortaQuandoNaoCabe()
    {
        var model = new TreeModel(SyntheticTreeProvider.CreateSessions(), new SyntheticTreeProvider());
        var view = new DatabaseTreeView();
        view.Tree.Model = model;
        view.Tree.ShowSizes = true;
        var session = model.Roots[0];
        await model.ExpandAsync(session);
        var db = session.Children![5];
        await model.ExpandAsync(db);
        var tables = TreeModel.ObjectsOf(db).Where(t => t.Name.Length >= 12).ToList();
        var noSize = tables[0];
        var withSize = tables[1];
        noSize.SizeBytes = null;
        withSize.SizeBytes = 123_456;
        var window = new Window { Content = view, Width = 230, Height = 600 };
        window.Show();
        window.CaptureRenderedFrame();

        // Sem tamanho, o nome usa a largura toda e cabe; com tamanho, a coluna Size fica reservada e o nome corta.
        Assert.False(view.Tree.IsNameClipped(noSize), noSize.Name);
        Assert.True(view.Tree.IsNameClipped(withSize), withSize.Name);
        window.Close();
    }
}
