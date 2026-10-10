using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit.v3;

[assembly: Eunomia.UI.Headless.Tests.UnexpectedDialogGuard]

namespace Eunomia.UI.Headless.Tests;

/// <summary>
/// Roadmap 2.4: a modal dialog the test did not expect (an error, a question) waits for a click that never comes and,
/// on the UI thread shared by the headless tests, used to hang the whole run. This guard closes any window opened
/// with <c>ShowDialog</c> as soon as it opens (the waiting code gets the default result, as with a Cancel) and fails the
/// test with the dialog's title and text. Tests answer questions through the MainWindow <c>*Override</c> hooks; a
/// dialog that is part of the verified behaviour is consumed with <see cref="TakeOpened"/>, and a dialog the test
/// drives (Find text) is allowed with <see cref="Allow"/>.
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

    /// <summary>Dialogs closed by the guard since the last call (<c>Type "Title": texts</c>).</summary>
    internal static IReadOnlyList<string> TakeOpened()
    {
        lock (Gate)
        {
            var copy = Opened.ToList();
            Opened.Clear();
            return copy;
        }
    }

    /// <summary>Dialogs of this type stay open until the end of the test (the test drives the dialog).</summary>
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
