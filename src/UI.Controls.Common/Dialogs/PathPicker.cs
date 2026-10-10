using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Eunomia.UI.Controls.Common.Dialogs;

/// <summary>
/// Seletores de ficheiros e pastas do sistema, devolvendo caminhos locais (Fase 14). Onde o sistema não tem seletor
/// (Linux sem <c>xdg-desktop-portal</c> nem GTK) ou ele falha, pede o caminho numa caixa de texto em vez de não fazer
/// nada, para abrir, gravar e exportar funcionarem sempre.
/// </summary>
public static class PathPicker
{
    private const string NoPickerNote = "No system file dialog is available. Type the full path:";

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(TopLevel owner, FilePickerOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        if (owner.StorageProvider is { CanOpen: true } storage)
        {
            try
            {
                var files = await storage.OpenFilePickerAsync(options).ConfigureAwait(true);
                return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Portal do sistema indisponível a meio: segue para a caixa de texto.
            }
        }
        return await AskAsync(owner, options.Title ?? "Open file", string.Empty).ConfigureAwait(true) is { } path ? [path] : [];
    }

    public static async Task<string?> SaveFileAsync(TopLevel owner, FilePickerSaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        if (owner.StorageProvider is { CanSave: true } storage)
        {
            try
            {
                return (await storage.SaveFilePickerAsync(options).ConfigureAwait(true))?.TryGetLocalPath();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
        var suggested = options.SuggestedFileName ?? string.Empty;
        return await AskAsync(owner, options.Title ?? "Save file", suggested.Length > 0 ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), suggested) : string.Empty).ConfigureAwait(true);
    }

    public static async Task<string?> OpenFolderAsync(TopLevel owner, FolderPickerOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        if (owner.StorageProvider is { CanPickFolder: true } storage)
        {
            try
            {
                var folders = await storage.OpenFolderPickerAsync(options).ConfigureAwait(true);
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
        return await AskAsync(owner, options.Title ?? "Select folder", string.Empty).ConfigureAwait(true);
    }

    private static async Task<string?> AskAsync(TopLevel owner, string title, string value)
    {
        if (owner is not Window window)
        {
            return null;
        }
        var text = await InputDialog.ShowAsync(window, title, NoPickerNote, value).ConfigureAwait(true);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
