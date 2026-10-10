using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Eunomia.Engine.Results;
using Eunomia.Presentation.Logging;
using Eunomia.UI.Controls.Common.TreeList;

namespace Eunomia.UI.Shell;

/// <summary>
/// Filter panel inferior (Ctrl+Alt+F; anexo D B11; FAM-G14): regex local, sem maiúsculas, sobre as linhas carregadas da
/// lista ativa (Data grid, result tab da query tab, listas do Host e do Database tab), 500 ms depois da última tecla. O texto fica guardado por
/// lista; "x out of y matching. z hidden."; regex inválida vai para o log e mostra tudo; o Data grid limpa-o a cada
/// recarga.
/// </summary>
public sealed partial class MainWindow
{
    private const string DataGridFilterKey = "data";

    private readonly Dictionary<object, string> _localFilterTexts = [];
    private DispatcherTimer? _localFilterTimer;
    private CancellationTokenSource? _localFilterScan;
    private object? _localFilterTarget;

    /// <summary>O Data grid mostra uma vista filtrada pelo filtro local (limpar repõe a fonte inteira).</summary>
    private bool _dataGridFiltered;

    /// <summary>Lista onde se escreveu por último na caixa (a que o temporizador de 500 ms filtra).</summary>
    private object? _localFilterTyped;

    private void InitLocalFilter()
    {
        _localFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _localFilterTimer.Tick += (_, _) =>
        {
            _localFilterTimer.Stop();
            // Só na lista onde se escreveu: mudar de tab a meio dos 500 ms não reaplica o filtro da lista seguinte.
            if (ReferenceEquals(LocalFilterTarget(), _localFilterTyped))
            {
                _ = ApplyLocalFilterAsync();
            }
        };
        _filterBox.TextChanged += (_, _) =>
        {
            // O TextChanged do Avalonia chega depois da mudança (assíncrono): um texto posto pelo código (troca de lista,
            // recarga do Data grid) já está guardado e não volta a aplicar o filtro. Antes, 500 ms depois de abrir o Data
            // tab ou de recarregar, o filtro vazio repunha a fonte do grid e o foco e o scroll voltavam à primeira linha.
            var text = _filterBox.Text ?? string.Empty;
            var target = LocalFilterTarget();
            if (target is null || string.Equals(_localFilterTexts.GetValueOrDefault(target) ?? string.Empty, text, StringComparison.Ordinal))
            {
                return;
            }
            _localFilterTexts[target] = text;
            _localFilterTyped = target;
            _localFilterTimer.Stop();
            _localFilterTimer.Start();
        };
        _tabs.SelectionChanged += (_, _) => SyncLocalFilterBox();
    }

    /// <summary>Lista que o filter panel filtra agora (chave do texto guardado), ou <c>null</c>.</summary>
    private object? LocalFilterTarget()
    {
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DataTab))
        {
            return DataGridFilterKey;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.HostTab) && _hostView is { } host)
        {
            return ReferenceEquals(host.SelectedItem, host.ProcessesTab) ? host.ProcessList : host.DatabaseList;
        }
        if (ReferenceEquals(_tabs.SelectedItem, _tabs.DatabaseTab) && _databaseView is { } database)
        {
            return database.List;
        }
        return ActiveResultView;
    }

    /// <summary>Mudou a lista ativa: a caixa mostra o texto guardado dessa lista.</summary>
    private void SyncLocalFilterBox()
    {
        var target = LocalFilterTarget();
        if (ReferenceEquals(target, _localFilterTarget))
        {
            return;
        }
        _localFilterTarget = target;
        _filterBox.Text = target is not null ? _localFilterTexts.GetValueOrDefault(target) ?? string.Empty : string.Empty;
        _filterInfo.Text = string.Empty;
    }

    /// <summary>O Data grid recarregou: o filtro local é limpo (anexo D B11).</summary>
    private void ResetDataGridFilter()
    {
        _localFilterTexts.Remove(DataGridFilterKey);
        _localFilterScan?.Cancel();
        _dataGridFiltered = false;
        if (ReferenceEquals(LocalFilterTarget(), DataGridFilterKey))
        {
            _filterBox.Text = string.Empty;
            _filterInfo.Text = string.Empty;
        }
    }

    private Regex? LocalFilterRegex(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        try
        {
            return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
            return null;
        }
    }

    private static string MatchText(long matching, long total) =>
        string.Create(CultureInfo.CurrentCulture, $"{matching:N0} out of {total:N0} matching. {total - matching:N0} hidden.");

    internal async Task ApplyLocalFilterAsync()
    {
        var target = LocalFilterTarget();
        var text = _filterBox.Text ?? string.Empty;
        var regex = LocalFilterRegex(text);
        switch (target)
        {
            case TreeListControl list:
                list.SetFilter(regex is null ? null : node => node.Cells.Any(c => regex.IsMatch(c)));
                if (regex is null)
                {
                    _filterInfo.Text = string.Empty;
                }
                else
                {
                    var nodes = list.AllNodes().ToList();
                    _filterInfo.Text = MatchText(nodes.Count(n => n.Cells.Any(c => regex.IsMatch(c))), nodes.Count);
                }
                break;
            case ResultView entry:
                _localFilterScan?.Cancel();
                var resultScan = new CancellationTokenSource();
                _localFilterScan = resultScan;
                var view = entry.View;
                if (regex is null)
                {
                    entry.Grid.Control.SetSource(view);
                    _filterInfo.Text = string.Empty;
                    return;
                }
                _context.Status.Busy = "Filtering rows ...";
                try
                {
                    var filteredResult = await FilteredRowSource.CreateAsync(view, regex, resultScan.Token);
                    if (resultScan.IsCancellationRequested || !ReferenceEquals(entry.View, view))
                    {
                        return;
                    }
                    entry.Grid.Control.SetSource(filteredResult);
                    _filterInfo.Text = MatchText(filteredResult.MatchCount, filteredResult.TotalRows);
                }
                catch (OperationCanceledException)
                {
                }
                catch (RegexMatchTimeoutException)
                {
                    _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
                }
                finally
                {
                    _context.Status.SetIdle();
                }
                break;
            case DataGridFilterKey when _browser is { } browser && _browserRows is { } source:
                _localFilterScan?.Cancel();
                var scan = new CancellationTokenSource();
                _localFilterScan = scan;
                if (regex is null)
                {
                    // Sem filtro aplicado, nada a repor: SetSource levava o foco e o scroll para a primeira linha.
                    if (_dataGridFiltered)
                    {
                        DataGrid.SetSource(new ProjectedRowSource(source, _dataVisibleColumns));
                        _dataGridFiltered = false;
                    }
                    _filterInfo.Text = string.Empty;
                    return;
                }
                _context.Status.Busy = "Filtering rows ...";
                try
                {
                    var filtered = await FilteredRowSource.CreateAsync(source, regex, scan.Token);
                    if (scan.IsCancellationRequested || !ReferenceEquals(_browser, browser))
                    {
                        return;
                    }
                    DataGrid.SetSource(new ProjectedRowSource(filtered, _dataVisibleColumns));
                    _dataGridFiltered = true;
                    _filterInfo.Text = MatchText(filtered.MatchCount, filtered.TotalRows);
                }
                catch (OperationCanceledException)
                {
                }
                catch (RegexMatchTimeoutException)
                {
                    _context.Log.Append(LogCategory.Info, "Filter text is not a valid regular expression");
                }
                finally
                {
                    _context.Status.SetIdle();
                }
                break;
        }
    }
}
