using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit.v3;

[assembly: Eunomia.UI.Headless.Tests.UnexpectedDialogGuard]

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Roadmap 2.4: um diálogo modal que o teste não esperava (erro, pergunta) fica à espera de um clique que nunca chega e,
/// na UI thread partilhada pelos testes headless, pendurava a corrida inteira. Este guarda fecha qualquer janela aberta
/// com <c>ShowDialog</c> logo que abre (o código à espera recebe o resultado por omissão, como num Cancel) e faz falhar o
/// teste com o título e o texto do diálogo. Os testes respondem às perguntas pelas <c>*Override</c> do MainWindow; um
/// diálogo que faz parte do comportamento verificado é consumido com <see cref="TakeOpened"/>, e um diálogo que o teste
/// conduz (Find text) é autorizado com <see cref="Allow"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class UnexpectedDialogGuardAttribute : BeforeAfterTestAttribute
{
    private static readonly Lock Gate = new();
    private static readonly List<string> Opened = [];
    private static readonly HashSet<Type> Allowed = [];
    private static int _installed;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 0)
        {
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnOpened(window));
        }
        lock (Gate)
        {
            Opened.Clear();
            Allowed.Clear();
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var opened = TakeOpened();
        lock (Gate)
        {
            Allowed.Clear();
        }
        if (opened.Count > 0)
        {
            throw new InvalidOperationException(
                "Diálogo modal inesperado (responder pelas *Override do MainWindow): " + string.Join(" | ", opened));
        }
    }

    /// <summary>Diálogos fechados pelo guarda desde a última chamada (<c>Tipo "Título": textos</c>).</summary>
    internal static IReadOnlyList<string> TakeOpened()
    {
        lock (Gate)
        {
            var copy = Opened.ToList();
            Opened.Clear();
            return copy;
        }
    }

    /// <summary>Diálogos deste tipo ficam abertos até ao fim do teste (o teste conduz o diálogo).</summary>
    internal static void Allow(Type dialogType)
    {
        lock (Gate)
        {
            Allowed.Add(dialogType);
        }
    }

    private static void OnOpened(Window window)
    {
        if (!window.IsDialog)
        {
            return;
        }
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t));
        lock (Gate)
        {
            if (Allowed.Contains(window.GetType()))
            {
                return;
            }
            Opened.Add($"{window.GetType().Name} \"{window.Title}\": {string.Join(" / ", texts)}");
        }
        Dispatcher.UIThread.Post(() => window.Close());
    }
}
