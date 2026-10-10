using Eunomia.App.Secrets;
using Eunomia.App.Sessions;

namespace Eunomia.Presentation.Sessions;

/// <summary>Resposta ao "Save modifications?" (Yes / No / Cancel).</summary>
public enum SavePrompt
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Estado do Session Manager (FAM-S01, S03; anexo A §1): lista de sessões e folders, sessão selecionada com um
/// rascunho editável, deteção de alterações (Save só ativo com alterações; "Save modifications?" ao mudar de sessão,
/// exceto quando só a password mudou, que é usada no Open e depois descartada), New/Save/Delete/Duplicate/Rename/Move
/// gravados de imediato, e passwords no cofre de segredos.
/// </summary>
public sealed class SessionManagerModel(SessionCatalog catalog, ISessionStore store, ISecretStore secrets, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string _storedPassword = string.Empty;
    private string _storedSshPassword = string.Empty;

    public SessionCatalog Catalog { get; } = catalog;

    /// <summary>Sessão selecionada (a gravada), ou <c>null</c> quando é um folder ou nada.</summary>
    public SessionDefinition? Selected { get; private set; }

    /// <summary>Folder selecionado (quando não é uma sessão).</summary>
    public IReadOnlyList<string>? SelectedFolder { get; private set; }

    /// <summary>Valores do formulário (cópia editável de <see cref="Selected"/>).</summary>
    public SessionDefinition? Draft { get; private set; }

    /// <summary>Password escrita no formulário.</summary>
    public string Password { get; set; } = string.Empty;

    public string SshPassword { get; set; } = string.Empty;

    /// <summary>Há alterações por gravar (o botão Save fica ativo).</summary>
    public bool IsModified =>
        Selected is not null && Draft is not null
        && (!Selected.HasSameSettings(Draft) || Password != _storedPassword || SshPassword != _storedSshPassword);

    /// <summary>Alterações que justificam o "Save modifications?" (a password sozinha não).</summary>
    public bool NeedsSavePrompt =>
        Selected is not null && Draft is not null && (!Selected.HasSameSettings(Draft) || SshPassword != _storedSshPassword);

    /// <summary>Folder onde ficam as sessões e folders novos "in selected folder".</summary>
    public IReadOnlyList<string> CurrentFolder => Selected?.Folder ?? SelectedFolder ?? [];

    /// <summary>Seleciona uma sessão (ou nada). O chamador já tratou do "Save modifications?".</summary>
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

    /// <summary>Save (Ctrl+S): grava os campos e as passwords; a cor passa logo para as sessões abertas (o shell trata).</summary>
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

    /// <summary>Descarta o rascunho (No no "Save modifications?").</summary>
    public void Discard() => SelectSession(Selected);

    /// <summary>New › Session in root folder / in selected folder: gravada logo, para rename.</summary>
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

    /// <summary>Delete (Del): a sessão, ou o folder com todas as sessões lá dentro; limpa as credenciais.</summary>
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

    /// <summary>Duplicate / save as (F12): grava os valores do formulário como sessão nova.</summary>
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

    /// <summary>Drag &amp; drop: largar num folder move para lá; numa sessão, para o folder dela; fora, para a raiz.</summary>
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

    /// <summary>Valores para o Open (os do formulário, mesmo não gravados) e a password escrita.</summary>
    public (SessionDefinition Values, LoginCredentials? Credentials) ValuesForOpen()
    {
        var values = Draft ?? throw new InvalidOperationException("No session selected.");
        var credentials = values.LoginPrompt ? null : new LoginCredentials(values.User, Password);
        return (values, credentials);
    }

    /// <summary>Depois de um Open (com ou sem sucesso): estatísticas da sessão gravada atualizadas e persistidas.</summary>
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

    /// <summary>"Last connect" como o HeidiSQL o mostra: "há X" (o tooltip tem a data exata).</summary>
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
