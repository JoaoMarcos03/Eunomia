using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Eunomia.UI.Controls.Common.Dialogs;

/// <summary>
/// System file and folder pickers, returning local paths (phase 14). Where the system has no picker
/// (Linux without <c>xdg-desktop-portal</c> or GTK) or it fails, asks for the path in a text box instead of doing
/// nothing, so open, save and export always work.
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
                // System portal unavailable midway: moves on to the text box.
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
