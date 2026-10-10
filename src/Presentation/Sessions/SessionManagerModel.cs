using Eunomia.App.Secrets;
using Eunomia.App.Sessions;

namespace Eunomia.Presentation.Sessions;

/// <summary>Answer to "Save modifications?" (Yes / No / Cancel).</summary>
public enum SavePrompt
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Session Manager state (FAM-S01, S03; appendix A §1): list of sessions and folders, selected session with an
/// editable draft, change detection (Save only enabled with changes; "Save modifications?" when switching sessions,
/// except when only the password changed, which is used for Open and then discarded), New/Save/Delete/Duplicate/Rename/Move
/// saved immediately, and passwords in the secret vault.
/// </summary>
public sealed class SessionManagerModel(SessionCatalog catalog, ISessionStore store, ISecretStore secrets, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string _storedPassword = string.Empty;
    private string _storedSshPassword = string.Empty;

    public SessionCatalog Catalog { get; } = catalog;

    /// <summary>Selected session (the saved one), or <c>null</c> when it is a folder or nothing.</summary>
    public SessionDefinition? Selected { get; private set; }

    /// <summary>Selected folder (when it is not a session).</summary>
    public IReadOnlyList<string>? SelectedFolder { get; private set; }

    /// <summary>Form values (editable copy of <see cref="Selected"/>).</summary>
    public SessionDefinition? Draft { get; private set; }

    /// <summary>Password typed in the form.</summary>
    public string Password { get; set; } = string.Empty;

    public string SshPassword { get; set; } = string.Empty;

    /// <summary>There are unsaved changes (the Save button is enabled).</summary>
    public bool IsModified =>
        Selected is not null && Draft is not null
        && (!Selected.HasSameSettings(Draft) || Password != _storedPassword || SshPassword != _storedSshPassword);

    /// <summary>Changes that justify "Save modifications?" (the password alone does not).</summary>
    public bool NeedsSavePrompt =>
        Selected is not null && Draft is not null && (!Selected.HasSameSettings(Draft) || SshPassword != _storedSshPassword);

    /// <summary>Folder where new sessions and folders go "in selected folder".</summary>
    public IReadOnlyList<string> CurrentFolder => Selected?.Folder ?? SelectedFolder ?? [];

    /// <summary>Selects a session (or nothing). The caller has already handled "Save modifications?".</summary>
    public void SelectSession(SessionDefinition? session)
    {
        Selected = session;
        SelectedFolder = null;
        Draft = session?.Clone();
        _storedPassword = session is null ? string.Empty : secrets.Get(SecretKeys.Password(session.Id)) ?? string.Empty;
        _storedSshPassword = session is null ? string.Empty : secrets.Get(SecretKeys.SshPassword(session.Id)) ?? string.Empty;
        Password = _storedPassword;
        SshPassword = _storedSshPassword;
    }

    public void SelectFolder(IReadOnlyList<string>? folder)
    {
        SelectSession(null);
        SelectedFolder = folder;
    }

    /// <summary>Save (Ctrl+S): saves the fields and the passwords; the colour goes straight to the open sessions (the shell handles it).</summary>
    public void Save()
    {
        if (Selected is null || Draft is null)
        {
            return;
        }
        Selected.CopySettingsFrom(Draft);
        if (!Selected.LoginPrompt)
        {
            secrets.Set(SecretKeys.Password(Selected.Id), Password);
            Selected.PasswordSaved = true;
        }
        else
        {
            secrets.Delete(SecretKeys.Password(Selected.Id));
            Selected.PasswordSaved = false;
        }
        if (SshPassword.Length > 0)
        {
            secrets.Set(SecretKeys.SshPassword(Selected.Id), SshPassword);
        }
        else
        {
            secrets.Delete(SecretKeys.SshPassword(Selected.Id));
        }
        _storedPassword = Password;
        _storedSshPassword = SshPassword;
        Selected.Statistics.Created ??= _clock.GetUtcNow();
        Persist();
    }

    /// <summary>Discards the draft (No on "Save modifications?").</summary>
    public void Discard() => SelectSession(Selected);

    /// <summary>New › Session in root folder / in selected folder: saved right away, ready for rename.</summary>
    public SessionDefinition NewSession(bool inSelectedFolder)
    {
        var session = Catalog.CreateSession(inSelectedFolder ? CurrentFolder : [], _clock.GetUtcNow());
        secrets.Set(SecretKeys.Password(session.Id), string.Empty);
        session.PasswordSaved = true;
        Persist();
        SelectSession(session);
        return session;
    }

    /// <summary>New › Folder in root folder / in selected folder.</summary>
    public IReadOnlyList<string> NewFolder(bool inSelectedFolder)
    {
        var folder = Catalog.CreateFolder(inSelectedFolder ? CurrentFolder : []);
        Persist();
        SelectFolder(folder);
        return folder;
    }

    /// <summary>Delete (Del): the session, or the folder with every session inside it; cleans up the credentials.</summary>
    public void DeleteSelected()
    {
        IReadOnlyList<SessionDefinition> removed;
        if (Selected is not null)
        {
            Catalog.DeleteSession(Selected);
            removed = [Selected];
        }
        else if (SelectedFolder is { Count: > 0 } folder)
        {
            removed = Catalog.DeleteFolder(folder);
        }
        else
        {
            return;
        }
        foreach (var s in removed)
        {
            foreach (var key in SecretKeys.All(s.Id))
            {
                secrets.Delete(key);
            }
        }
        Persist();
        SelectSession(null);
    }

    /// <summary>Duplicate / save as (F12): saves the form values as a new session.</summary>
    public SessionDefinition Duplicate(string newName)
    {
        var values = Draft ?? throw new InvalidOperationException("No session selected.");
        var copy = Catalog.Duplicate(values, newName, _clock.GetUtcNow());
        if (!copy.LoginPrompt)
        {
            secrets.Set(SecretKeys.Password(copy.Id), Password);
            copy.PasswordSaved = true;
        }
        if (SshPassword.Length > 0)
        {
            secrets.Set(SecretKeys.SshPassword(copy.Id), SshPassword);
        }
        Persist();
        SelectSession(copy);
        return copy;
    }

    public string? Rename(SessionTreeNode node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        var error = node.Session is { } session ? Catalog.RenameSession(session, newName) : Catalog.RenameFolder(node.OwnPath, newName);
        if (error is null)
        {
            Persist();
            if (node.Session is { } s && ReferenceEquals(s, Selected) && Draft is not null)
            {
                Draft.Name = s.Name;
            }
        }
        return error;
    }

    /// <summary>Drag &amp; drop: dropping on a folder moves there; on a session, to its folder; outside, to the root.</summary>
    public string? Move(SessionTreeNode source, SessionTreeNode? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<string> folder = target is null ? [] : target.IsFolder ? target.OwnPath : target.FolderPath;
        var error = source.Session is { } session ? Catalog.MoveSession(session, folder) : Catalog.MoveFolder(source.OwnPath, folder);
        if (error is null)
        {
            Persist();
        }
        return error;
    }

    /// <summary>Values for Open (the form's, even when not saved) and the typed password.</summary>
    public (SessionDefinition Values, LoginCredentials? Credentials) ValuesForOpen()
    {
        var values = Draft ?? throw new InvalidOperationException("No session selected.");
        var credentials = values.LoginPrompt ? null : new LoginCredentials(values.User, Password);
        return (values, credentials);
    }

    /// <summary>After an Open (successful or not): the saved session's statistics updated and persisted.</summary>
    public void RecordConnectAttempt(SessionDefinition values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (Selected is not null && Selected.Id == values.Id)
        {
            Selected.Statistics = values.Statistics;
        }
        Persist();
    }

    public void Persist() => store.Save(Catalog);

    /// <summary>"Last connect" as HeidiSQL shows it: "X ago" (the tooltip has the exact date).</summary>
    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } value)
        {
            return "unknown or never";
        }
        var span = now - value;
        return span.TotalSeconds < 60 ? "a few seconds ago"
            : span.TotalMinutes < 60 ? Plural((int)span.TotalMinutes, "minute") + " ago"
            : span.TotalHours < 24 ? Plural((int)span.TotalHours, "hour") + " ago"
            : span.TotalDays < 30 ? Plural((int)span.TotalDays, "day") + " ago"
            : span.TotalDays < 365 ? Plural((int)(span.TotalDays / 30), "month") + " ago"
            : Plural((int)(span.TotalDays / 365), "year") + " ago";
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
