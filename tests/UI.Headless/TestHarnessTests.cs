using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Eunomia.UI.Controls.Common.Dialogs;
using Xunit.v3;

namespace Eunomia.UI.Headless.Tests;

/// <summary>Proteções da corrida headless (roadmap 2.4): diálogos modais inesperados e timeout por teste.</summary>
public sealed class TestHarnessTests
{
    [AvaloniaFact(Timeout = UiTimeouts.Default)]
    public async Task DialogoModalInesperadoEhFechadoERegistado()
    {
        var owner = new Window();
        owner.Show();
        MessageDialogResult result;
        try
        {
            result = await MessageDialog.ShowAsync(owner, "Error", "Something broke", MessageDialogButtons.Ok);
        }
        finally
        {
            owner.Close();
        }

        // Sem o guarda, este await nunca acabava. O registo é consumido aqui: neste teste o diálogo era o esperado.
        Assert.Equal(MessageDialogResult.None, result);
        Assert.Equal(["MessageDialog \"Error\": Something broke"], UnexpectedDialogGuardAttribute.TakeOpened());
    }

    [AvaloniaFact]
    public void JanelaNaoModalNaoEhFechada()
    {
        var window = new Window { Title = "Tool" };
        window.Show();

        Assert.True(window.IsVisible);
        Assert.Empty(UnexpectedDialogGuardAttribute.TakeOpened());
        window.Close();
    }

    [Fact]
    public void TestesAssincronosTemTimeout()
    {
        // Código de teste: corre sem trimming.
#pragma warning disable IL2026, IL2070
        var missing = typeof(TestHarnessTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(Task) || m.ReturnType == typeof(ValueTask))
            .Where(m => m.GetCustomAttributes(inherit: false).OfType<IFactAttribute>().Any(a => a.Timeout <= 0))
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();
#pragma warning restore IL2026, IL2070

        Assert.True(missing.Count == 0, "Sem Timeout = UiTimeouts.Default: " + string.Join(", ", missing));
    }
}
