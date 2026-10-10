using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// SQL help (F1, Help › SQL help): a janela de ajuda do servidor da sessão ativa, aberta no termo onde está o cursor do
/// editor focado (ou nas categorias). Uma só janela: F1 com ela aberta procura o termo novo nela.
/// </summary>
public sealed partial class MainWindow
{
    private SqlHelpWindow? _sqlHelp;

    /// <summary>Janela de ajuda aberta (testes).</summary>
    internal SqlHelpWindow? SqlHelp => _sqlHelp;

    private void InitSqlHelp()
    {
        _context.Commands.SetHandler("help.sql", _ => new ValueTask(ShowSqlHelpAsync()), () => ActiveSession is { Host.ServerServices: not null });
    }

    private async Task ShowSqlHelpAsync()
    {
        if (ActiveSession is not { } open)
        {
            return;
        }
        var keyword = WordAtCaret();
        if (_sqlHelp is { } window)
        {
            window.Activate();
            if (keyword is not null)
            {
                await window.ShowAsync(keyword);
            }
            return;
        }
        _sqlHelp = new SqlHelpWindow((term, ct) => open.Host.RunMetadataAsync((h, token) => h.Metadata.GetHelpAsync(term, token), ct), keyword);
        _sqlHelp.Closed += (_, _) => _sqlHelp = null;
        _sqlHelp.Show(this);
    }

    /// <summary>
    /// Palavra onde está o cursor (letras, dígitos e <c>_</c>) do editor SQL focado ou, sem foco num editor, do editor
    /// da query tab ativa; <c>null</c> sem palavra.
    /// </summary>
    private string? WordAtCaret()
    {
        if ((FocusedSqlEditor() ?? (_tabs.ActiveQueryTab is not null ? ActiveQueryView?.Editor : null)) is not { } editor)
        {
            return null;
        }
        var document = editor.Editor.Document;
        var caret = editor.Editor.CaretOffset;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = caret;
        while (start > 0 && IsWord(document.GetCharAt(start - 1)))
        {
            start--;
        }
        var end = caret;
        while (end < document.TextLength && IsWord(document.GetCharAt(end)))
        {
            end++;
        }
        return end > start ? document.GetText(start, end - start) : null;
    }
}
