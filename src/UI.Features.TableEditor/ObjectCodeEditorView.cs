using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Eunomia.Drivers.Abstractions;
using Eunomia.UI.Controls.SqlEditor;

namespace Eunomia.UI.Features.TableEditor;

/// <summary>
/// Editor por código de uma view, procedure, function, trigger ou event (Table tab): o <c>CREATE</c> num editor SQL,
/// com Discard e Save em baixo. Quem cria a vista grava (apaga e volta a criar o objeto).
/// </summary>
public sealed class ObjectCodeEditorView : DockPanel
{
    private readonly Button _save = new() { Content = "Save", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center, Name = "btnSaveObject" };
    private readonly Button _discard = new() { Content = "Discard", MinWidth = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
    private string _saved;

    public ObjectCodeEditorView(DbObjectKind kind, string code, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(code);
        Kind = kind;
        IsNew = isNew;
        _saved = code;
        Editor = new SqlEditorControl { Text = code };
        // O servidor devolve o CREATE de uma view numa só linha: quebrar as linhas longas lê-se melhor.
        Editor.Editor.WordWrap = true;

        var hint = new TextBlock
        {
            Text = isNew
                ? "Edit the CREATE statement and press Save to create the object."
                : "Edit the CREATE statement and press Save: the object is dropped and created again (the old one comes back if the new code fails).",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 2, 6),
        };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        bottom.Children.Add(_discard);
        bottom.Children.Add(_save);
        _save.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        _discard.Click += (_, _) => DiscardRequested?.Invoke(this, EventArgs.Empty);

        Margin = new Thickness(4);
        SetDock(hint, Dock.Top);
        SetDock(bottom, Dock.Bottom);
        Children.Add(hint);
        Children.Add(bottom);
        Children.Add(Editor);
    }

    public DbObjectKind Kind { get; }

    /// <summary>Objeto novo ("Create new ▸"), ainda não existe no servidor.</summary>
    public bool IsNew { get; }

    public SqlEditorControl Editor { get; }

    public string Code => Editor.Text;

    /// <summary>O código mudou desde que a vista abriu (ou desde <see cref="MarkSaved"/>).</summary>
    public bool IsModified => !string.Equals(Editor.Text, _saved, StringComparison.Ordinal);

    public event EventHandler? SaveRequested;

    public event EventHandler? DiscardRequested;

    public void MarkSaved() => _saved = Editor.Text;
}
