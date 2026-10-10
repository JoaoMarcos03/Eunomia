using System.Globalization;
using Avalonia.Platform.Storage;
using Eunomia.App.Query;
using Eunomia.App.Sessions;
using Eunomia.App.Transfer;
using Eunomia.Engine.Execution;
using Eunomia.Engine.Transfer;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.Dialogs;
using Eunomia.UI.Shell.Views;

namespace Eunomia.UI.Shell;

/// <summary>
/// Run SQL file (Fase 8; 27.1 #21; anexo B §4.1–4.2): "Run SQL file..." corre os ficheiros sem os abrir; o Load pergunta
/// com o "Opening large files" quando algum tem mais de 5 MiB. Cada ficheiro é um <see cref="SqlFileJob"/> numa ligação
/// de job, com a base atual da sessão, Stop on errors e "Send batch in one go" do menu SQL. O SQL log recebe o resumo de
/// cada ficheiro ("File X [partially] executed, with N queries and R affected rows") e os erros com nº e linha. Um job
/// parado (erro, cancel, crash) fica no journal: Resume na janela, ou a pergunta no arranque seguinte.
/// </summary>
public sealed partial class MainWindow
{
    private SqlFileRunWindow? _runWindow;

    /// <summary>Substitui o "Opening large files" (testes).</summary>
    internal Func<IReadOnlyList<(string Path, long Bytes)>, LargeFilesChoice>? LargeFilesOverride { get; set; }

    /// <summary>Ficheiros a correr em curso (os testes esperam por eles).</summary>
    internal Task RunningFiles { get; private set; } = Task.CompletedTask;

    internal SqlFileRunWindow? RunWindow => _runWindow;

    private void InitRunFile()
    {
        _context.Commands.SetHandler("query.runFile", _ => new ValueTask(RunSqlFileCommandAsync()), () => ActiveSession is { Host.State: SessionState.Ready } && _runWindow is null);
        Opened += async (_, _) => await ResumeInterruptedJobsAsync();
    }

    private async Task RunSqlFileCommandAsync()
    {
        IReadOnlyList<string> paths;
        if (OpenSqlFilesOverride is { } fake)
        {
            paths = fake();
        }
        else
        {
            paths = [.. await PathPicker.OpenFilesAsync(this, new FilePickerOpenOptions
            {
                Title = "Run SQL file",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("SQL files") { Patterns = ["*.sql"] }, FilePickerFileTypes.All],
            })];
        }
        if (paths.Count == 0)
        {
            return;
        }
        var encoding = LoadEncodingOverride is { } choose
            ? choose()
            : await ChoiceDialog.ShowAsync(this, "Run SQL file", "Encoding:", SqlFile.LoadEncodings, SqlFile.AutoDetect);
        if (encoding is not null)
        {
            await OpenSqlFilesAsync(paths, encoding, forceRun: true);
        }
    }

    /// <summary>
    /// Load ou Run: sem <paramref name="forceRun"/>, ficheiros com mais de 5 MiB levam ao "Opening large files" (correr,
    /// abrir no editor ou cancelar).
    /// </summary>
    private async Task OpenSqlFilesAsync(IReadOnlyList<string> paths, string encoding, bool forceRun)
    {
        if (!forceRun)
        {
            List<(string Path, long Bytes)> sizes;
            try
            {
                sizes = [.. paths.Select(p => (Path.GetFullPath(p), new FileInfo(p).Length))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowError(ex);
                return;
            }
            if (sizes.Any(s => s.Bytes > SqlFile.LargeFileBytes))
            {
                var choice = await AskLargeFilesAsync(sizes);
                if (choice == LargeFilesChoice.Cancel)
                {
                    return;
                }
                forceRun = choice == LargeFilesChoice.Run;
            }
        }
        if (forceRun)
        {
            await RunFilesNowAsync(paths, encoding);
            return;
        }
        foreach (var path in paths)
        {
            await LoadFileAsync(path, encoding, askLarge: false);
        }
    }

    private async Task<LargeFilesChoice> AskLargeFilesAsync(IReadOnlyList<(string Path, long Bytes)> sizes) =>
        LargeFilesOverride is { } fake ? fake(sizes) : await LargeFilesDialog.ShowAsync(this, sizes);

    private async Task RunFilesNowAsync(IReadOnlyList<string> paths, string encoding)
    {
        if (ActiveSession is not { Host.State: SessionState.Ready } open)
        {
            await MessageDialog.ShowAsync(this, "Run SQL file", "Connect to a session first.", MessageDialogButtons.Ok);
            return;
        }
        if (!await ConfirmWritesAsync(open, string.Create(CultureInfo.InvariantCulture,
                $"Run {paths.Count} SQL file(s) in database \"{open.Host.CurrentDatabase ?? "(none)"}\"?\n\n{string.Join("\n", paths.Select(Path.GetFileName))}")))
        {
            return;
        }
        RunningFiles = RunSqlFilesAsync(open, paths, encoding, resume: null);
        await RunningFiles;
    }

    /// <summary>Corre os ficheiros um a um na janela de progresso; um job parado espera por Resume ou Close.</summary>
    private async Task RunSqlFilesAsync(OpenSession open, IReadOnlyList<string> paths, string encoding, TransferJournalEntry? resume)
    {
        var journal = _context.TransferJournal;
        var total = paths.Sum(SizeOf) + (resume is null ? 0 : SizeOf(resume.Path));
        var window = new SqlFileRunWindow(total);
        _runWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_runWindow, window))
            {
                _runWindow = null;
            }
        };
        window.Show(this);
        var queue = new Queue<string>(paths);
        var started = DateTime.UtcNow;
        long done = 0;
        var processed = 0;
        while (resume is not null || queue.Count > 0)
        {
            SqlFileJob job;
            string path;
            if (resume is not null)
            {
                path = resume.Path;
                job = journal is null ? new SqlFileJob(open.Host, SqlFileJob.RequestOf(resume)) : new SqlFileJob(open.Host, resume, journal);
                resume = null;
            }
            else
            {
                path = Path.GetFullPath(queue.Dequeue());
                job = new SqlFileJob(open.Host, new SqlFileRequest
                {
                    Path = path,
                    Encoding = encoding,
                    Database = open.Host.CurrentDatabase,
                    StopOnErrors = _context.Settings.Query.StopOnErrors,
                    BatchInOneGo = _context.Settings.Query.BatchInOneGo,
                    Delimiter = _context.Settings.Query.Delimiter,
                    Session = open.Name,
                }, journal);
            }
            window.Start(path, done);
            var result = await RunFileJobAsync(window, job, path);
            if (result is { Completed: true })
            {
                done += SizeOf(path);
                processed++;
                continue;
            }
            var entry = journal?.List().FirstOrDefault(e => e.Id == job.Id);
            window.Finish(StopMessage(result), canResume: entry is not null);
            if (entry is null || !await WaitForResumeAsync(window))
            {
                return;
            }
            resume = journal!.List().FirstOrDefault(e => e.Id == job.Id) ?? entry;
        }
        var elapsed = DateTime.UtcNow - started;
        var summary = string.Create(CultureInfo.InvariantCulture, $"{processed} file(s) processed, in {elapsed.TotalSeconds:0.0} s");
        _context.Log.Append(LogCategory.Info, summary);
        window.Finish(summary + ".", canResume: false);
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static string StopMessage(SqlFileResult? result) => result switch
    {
        null => "The import failed (see the SQL log).",
        { Cancelled: true } => string.Create(CultureInfo.CurrentCulture, $"Cancelled after {result.Statements:N0} queries."),
        { StoppedOnError: true } => string.Create(CultureInfo.CurrentCulture, $"Stopped at query #{result.Statements + 1:N0}: {result.ErrorList.LastOrDefault()?.Message}"),
        _ => string.Empty,
    };

    private static Task<bool> WaitForResumeAsync(SqlFileRunWindow window)
    {
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResume(object? sender, EventArgs e) => resume.TrySetResult(true);
        window.ResumeRequested += OnResume;
        window.Closed += (_, _) => resume.TrySetResult(false);
        return resume.Task.ContinueWith(
            t =>
            {
                window.ResumeRequested -= OnResume;
                return t.Result;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Um ficheiro: o job fora da UI thread, o resumo e os erros no SQL log; <c>null</c> se o job falhou.</summary>
    private async Task<SqlFileResult?> RunFileJobAsync(SqlFileRunWindow window, SqlFileJob job, string path)
    {
        var progress = new Progress<SqlFileProgress>(window.Show);
        SqlFileResult result;
        try
        {
            result = await Task.Run(() => job.RunAsync(progress, window.Token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _context.Log.Append(LogCategory.Error, $"File {path}: {ex.Message}");
            window.Finish("Error: " + ex.Message, canResume: false);
            return null;
        }
        _context.Settings.Query.RecentFiles = SqlFile.AddRecent(_context.Settings.Query.RecentFiles, path);
        _context.Diagnostics.Write(Eunomia.App.Diagnostics.DiagnosticLevel.Info, "import",
            $"Run SQL file {path}: {result.Statements} statements, {result.Errors} errors, {result.BytesRead} bytes, completed: {result.Completed}");
        foreach (var error in result.ErrorList.Take(100))
        {
            _context.Log.Append(LogCategory.Error, string.Create(CultureInfo.InvariantCulture, $"Error in query #{error.Statement} (line {error.Line}): {error.Message}"));
        }
        _context.Log.Append(
            LogCategory.Info,
            string.Create(CultureInfo.InvariantCulture, $"File {path} {(result.Completed ? string.Empty : "partially ")}executed, with {result.Statements} queries and {result.AffectedRows} affected rows"));
        if (result.StoppedOnError)
        {
            _context.Log.Append(LogCategory.Info, "Notice: You can disable the \"Stop on errors in batch mode\" option to ignore such errors");
        }
        else if (result.Completed && result.Errors > 0)
        {
            window.Show(new SqlFileProgress(result.BytesRead, result.TotalBytes, result.Statements, result.Errors, result.AffectedRows, result.Elapsed));
            _context.Log.Append(LogCategory.Error, string.Create(CultureInfo.InvariantCulture,
                $"100% of your file has been processed, but there were {result.Errors} errors when executing {result.Statements} queries. Please check the SQL log panel."));
        }
        return result;
    }

    /// <summary>Jobs de import parados numa execução anterior: Resume (liga a sessão), descartar, ou deixar para depois.</summary>
    private async Task ResumeInterruptedJobsAsync()
    {
        if (_context.TransferJournal is not { } journal || Sessions is not { } sessions)
        {
            return;
        }
        foreach (var entry in journal.List().Where(e => e.Kind == TransferJobKind.SqlImport))
        {
            var percent = entry.InputSize > 0 ? entry.Offset * 100 / entry.InputSize : 0;
            var answer = await MessageDialog.ShowAsync(this, "Interrupted import",
                string.Create(CultureInfo.CurrentCulture, $"The import of file {entry.Path} into session \"{entry.Session}\" stopped after {entry.Count:N0} queries ({percent}%, line {entry.Line:N0}).")
                + (entry.LastStatement is { } last ? "\n\nLast executed query:\n" + last : string.Empty)
                + "\n\nResume it now? \"No\" discards it, \"Cancel\" keeps it for later.",
                MessageDialogButtons.YesNoCancel);
            if (answer == MessageDialogResult.No)
            {
                journal.Delete(entry.Id);
                continue;
            }
            if (answer != MessageDialogResult.Yes)
            {
                continue;
            }
            var definition = sessions.Catalog.FindByPath(entry.Session);
            var open = definition is null ? null : sessions.Connector.Find(definition.Id);
            if (definition is not null && open is not { Host.State: SessionState.Ready } && await ConnectAsync(this, definition, null, showHostTab: false))
            {
                open = sessions.Connector.Find(definition.Id);
            }
            if (open is not { Host.State: SessionState.Ready })
            {
                await MessageDialog.ShowAsync(this, "Interrupted import", $"Session \"{entry.Session}\" is not available.", MessageDialogButtons.Ok);
                continue;
            }
            RunningFiles = RunSqlFilesAsync(open, [], entry.Options.GetValueOrDefault("encoding") ?? SqlFile.AutoDetect, entry);
            await RunningFiles;
        }
    }
}
