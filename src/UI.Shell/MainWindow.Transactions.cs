using Avalonia.Threading;
using Eunomia.App.Sessions;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;

namespace Eunomia.UI.Shell;

/// <summary>
/// Transações da ligação primária (secção 14.5; Fase 14): a status bar mostra "Transaction open" / "Uncommitted changes";
/// desligar uma sessão ou fechar a janela com alterações por confirmar pergunta Commit (Yes), Rollback (No) ou Cancel;
/// uma ligação que cai com alterações por confirmar avisa que o servidor as desfez. Os pedidos de fecho passam todos por
/// <see cref="_closeGuards"/>, por ordem, para que uma pergunta não salte as outras.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<(Func<bool> Needed, Func<Task<bool>> Confirm)> _closeGuards = [];
    private bool _closeConfirmed;

    /// <summary>Testes: resposta à pergunta sobre a transação aberta (sem diálogo).</summary>
    internal Func<string, Task<MessageDialogResult>>? TransactionQuestionOverride { get; set; }

    private void InitCloseGuards()
    {
        AddRunningJobGuard();
        Closing += (_, e) =>
        {
            if (_closeConfirmed || !_closeGuards.Any(g => g.Needed()))
            {
                return;
            }
            e.Cancel = true;
            _ = RunCloseGuardsAsync();
        };
        _closeGuards.Add((HasOpenTransactions, ResolveAllOpenTransactionsAsync));
    }

    private bool HasOpenTransactions() => Sessions?.Connector.Open.Any(o => o.Host.Transactions.HasChanges) == true;

    private async Task<bool> ResolveAllOpenTransactionsAsync()
    {
        foreach (var open in Sessions!.Connector.Open.ToList())
        {
            if (!await ResolveOpenTransactionAsync(open))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Guarda de fecho com um Run SQL file a correr (a janela de progresso não é modal).</summary>
    private void AddRunningJobGuard() => _closeGuards.Add((
        () => !RunningFiles.IsCompleted,
        async () => (await ConfirmAsync("Confirm",
            "A SQL file is still running. Closing interrupts it; the next time Eunomia starts it offers to resume from the last statement it recorded.\n\nClose anyway?")).Ok));

    private async Task RunCloseGuardsAsync()
    {
        foreach (var (needed, confirm) in _closeGuards.ToList())
        {
            if (needed() && !await confirm())
            {
                return;
            }
        }
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Avisos de transação perdida quando a ligação primária de uma sessão cai.</summary>
    private void WatchTransactions(OpenSession open)
    {
        open.Host.Transactions.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateStatusBar);
        open.Host.TransactionLost += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var message = $"The connection of session \"{open.Name}\" was lost while a transaction with changes was open. " +
                          "The server rolled the transaction back: the changes made since it started were NOT saved.";
            _context.Diagnostics.Write(App.Diagnostics.DiagnosticLevel.Warning, "transaction", "Transaction lost with the connection", null);
            if (EditErrorOverride is { } report)
            {
                report(message);
            }
            else
            {
                _ = MessageDialog.ShowAsync(this, "Transaction rolled back", message, MessageDialogButtons.Ok);
            }
        });
    }

    /// <summary>
    /// Sessão com alterações por confirmar: pergunta Commit (Yes) / Rollback (No) / Cancel e corre o escolhido na ligação
    /// primária. Devolve se pode seguir (desligar ou fechar).
    /// </summary>
    internal async Task<bool> ResolveOpenTransactionAsync(OpenSession open)
    {
        if (!open.Host.Transactions.HasChanges)
        {
            return true;
        }
        var question = $"Session \"{open.Name}\" has uncommitted changes in an open transaction.\n\n" +
                       "Yes: COMMIT them.\nNo: ROLLBACK (the changes are discarded).\nCancel: keep the session open.";
        var answer = TransactionQuestionOverride is { } ask
            ? await ask(question)
            : await MessageDialog.ShowAsync(this, "Open transaction", question, MessageDialogButtons.YesNoCancel);
        if (answer is not (MessageDialogResult.Yes or MessageDialogResult.No))
        {
            return false;
        }
        var sql = answer == MessageDialogResult.Yes ? "COMMIT" : "ROLLBACK";
        try
        {
            await open.Host.RunPrimaryAsync((h, ct) => h.ExecuteNonQueryAsync(sql, ct), CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is Drivers.Abstractions.DbOperationException or Engine.Execution.SessionOperationException or InvalidOperationException)
        {
            _context.Log.Append(LogCategory.Error, ex.Message);
            ShowEditError($"{sql} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Antes de uma operação que escreve (Run SQL file, import de CSV): numa sessão read-only diz que não pode; com
    /// "Ask before running statements that change data" pergunta. Devolve se pode seguir.
    /// </summary>
    internal async Task<bool> ConfirmWritesAsync(OpenSession session, string question)
    {
        if (session.Definition.ReadOnly)
        {
            ShowEditError(ReadOnlyMessage(session));
            return false;
        }
        return !session.Definition.ConfirmChanges || (await ConfirmAsync("Confirm changes", question + SessionLine(session))).Ok;
    }

    /// <summary>Texto do painel da transação na status bar: vazio, "Transaction open" ou "Uncommitted changes".</summary>
    private static string TransactionStatus(OpenSession session) =>
        session.Host.Transactions.HasChanges ? "Uncommitted changes"
        : session.Host.Transactions.IsOpen ? "Transaction open"
        : string.Empty;
}
