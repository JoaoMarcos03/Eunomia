using System.Text.RegularExpressions;

namespace Eunomia.App.Sessions;

/// <summary>Node of the Session Manager list: folder or session.</summary>
public sealed class SessionTreeNode
{
    public SessionTreeNode(string name, IReadOnlyList<string> folderPath, SessionDefinition? session)
    {
        Name = name;
        FolderPath = folderPath;
        Session = session;
    }

    public string Name { get; }

    /// <summary>Path of the folder the node is in (for a folder, the parent's path).</summary>
    public IReadOnlyList<string> FolderPath { get; }

    public SessionDefinition? Session { get; }

    public bool IsFolder => Session is null;

    /// <summary>Full path of the folder (folders only).</summary>
    public IReadOnlyList<string> OwnPath => [.. FolderPath, Name];

    public List<SessionTreeNode> Children { get; } = [];
}

/// <summary>
/// Sessions and folders of the Session Manager (FAM-S01, S03; appendix A §1.3–1.5), with HeidiSQL's rules: names unique
/// among siblings, case-insensitively (the Windows registry is case-insensitive, so a rename that only changes the
/// case is refused), <c>\</c> becomes <c>-</c>, new nodes are called <c>Unnamed</c>, <c>Unnamed-1</c>…, folders
/// without manual ordering. Persisted through <see cref="ISessionStore"/>.
/// </summary>
public sealed class SessionCatalog
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<SessionDefinition> Sessions { get; set; } = [];

    /// <summary>Explicit folders (empty ones too); those with sessions exist implicitly.</summary>
    public List<List<string>> Folders { get; set; } = [];

    public SessionDefinition? Find(Guid id) => Sessions.FirstOrDefault(s => s.Id == id);

    public SessionDefinition? FindByPath(string path) =>
        Sessions.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every folder (explicit and implicit), without duplicates.</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllFolders()
    {
        var result = new List<IReadOnlyList<string>>();
        void Add(IReadOnlyList<string> path)
        {
            for (var depth = 1; depth <= path.Count; depth++)
            {
                var prefix = path.Take(depth).ToList();
                if (!result.Any(f => SamePath(f, prefix)))
                {
                    result.Add(prefix);
                }
            }
        }
        foreach (var folder in Folders)
        {
            Add(folder);
        }
        foreach (var session in Sessions)
        {
            Add(session.Folder);
        }
        return result;
    }

    public bool FolderExists(IReadOnlyList<string> path) => path.Count == 0 || AllFolders().Any(f => SamePath(f, path));

    /// <summary>Names of a folder's direct children (sessions and subfolders).</summary>
    public IEnumerable<string> ChildNames(IReadOnlyList<string> folder) =>
        Sessions.Where(s => SamePath(s.Folder, folder)).Select(s => s.Name)
            .Concat(AllFolders().Where(f => f.Count == folder.Count + 1 && IsPrefix(folder, f)).Select(f => f[^1]));

    /// <summary>First free name: <c>Unnamed</c>, <c>Unnamed-1</c>, <c>Unnamed-2</c>…</summary>
    public string UniqueName(IReadOnlyList<string> folder, string baseName = "Unnamed")
    {
        var names = ChildNames(folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }
        for (var i = 1; ; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>New › Session (saved right away, as in HeidiSQL).</summary>
    public SessionDefinition CreateSession(IReadOnlyList<string> folder, DateTimeOffset now)
    {
        var session = new SessionDefinition { Name = UniqueName(folder), Folder = [.. folder] };
        session.Statistics.Created = now;
        Sessions.Add(session);
        return session;
    }

    /// <summary>New › Folder.</summary>
    public IReadOnlyList<string> CreateFolder(IReadOnlyList<string> parent)
    {
        List<string> path = [.. parent, UniqueName(parent)];
        Folders.Add(path);
        return path;
    }

    /// <summary>Valid name to save: <c>\</c> becomes <c>-</c> (HeidiSQL issue #682), spaces trimmed.</summary>
    public static string NormalizeName(string name) => name.Replace('\\', '-').Trim();

    /// <summary>Renames a session. Returns the error to show, or <c>null</c> if it went well.</summary>
    public string? RenameSession(SessionDefinition session, string newName)
    {
        var name = NormalizeName(newName);
        if (name.Length == 0)
        {
            return "Name must not be empty.";
        }
        if (string.Equals(name, session.Name, StringComparison.Ordinal))
        {
            return null;
        }
        if (ChildNames(session.Folder).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(name, session.Name, StringComparison.OrdinalIgnoreCase)
                ? "Renaming only the letter case is not supported. Please rename to something different first, then to the desired name."
                : $"Session \"{name}\" already exists!";
        }
        session.Name = name;
        return null;
    }

    /// <summary>Renames a folder: changes the path of every session and subfolder.</summary>
    public string? RenameFolder(IReadOnlyList<string> path, string newName)
    {
        var name = NormalizeName(newName);
        if (name.Length == 0)
        {
            return "Name must not be empty.";
        }
        var parent = path.Take(path.Count - 1).ToList();
        if (string.Equals(name, path[^1], StringComparison.Ordinal))
        {
            return null;
        }
        if (ChildNames(parent).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(name, path[^1], StringComparison.OrdinalIgnoreCase)
                ? "Renaming only the letter case is not supported. Please rename to something different first, then to the desired name."
                : $"Folder \"{name}\" already exists!";
        }
        List<string> renamed = [.. parent, name];
        Reparent(path, renamed);
        return null;
    }

    /// <summary>
    /// Drag &amp; drop: moves a session to a folder. Refuses dropping it on the folder it is already in, and duplicate names.
    /// </summary>
    public string? MoveSession(SessionDefinition session, IReadOnlyList<string> target)
    {
        if (SamePath(session.Folder, target))
        {
            return "Already in this folder.";
        }
        if (ChildNames(target).Any(n => string.Equals(n, session.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Session \"{session.Name}\" already exists!";
        }
        session.Folder = [.. target];
        return null;
    }

    /// <summary>Moves a folder; refuses its own parent, itself and its descendants.</summary>
    public string? MoveFolder(IReadOnlyList<string> path, IReadOnlyList<string> target)
    {
        var parent = path.Take(path.Count - 1).ToList();
        if (SamePath(parent, target) || IsPrefix(path, target))
        {
            return "Cannot move a folder into itself, its descendants or its current parent.";
        }
        if (ChildNames(target).Any(n => string.Equals(n, path[^1], StringComparison.OrdinalIgnoreCase)))
        {
            return $"Folder \"{path[^1]}\" already exists!";
        }
        Reparent(path, [.. target, path[^1]]);
        return null;
    }

    /// <summary>Deletes a session.</summary>
    public void DeleteSession(SessionDefinition session) => Sessions.Remove(session);

    /// <summary>Deletes a folder: deletes every session inside it (returned so their credentials can be cleaned up).</summary>
    public IReadOnlyList<SessionDefinition> DeleteFolder(IReadOnlyList<string> path)
    {
        var removed = Sessions.Where(s => IsPrefix(path, s.Folder)).ToList();
        Sessions.RemoveAll(s => removed.Contains(s));
        Folders.RemoveAll(f => IsPrefix(path, f));
        return removed;
    }

    /// <summary>Duplicate / save as (F12): a copy of the values with another name and another id, in the same folder.</summary>
    public SessionDefinition Duplicate(SessionDefinition values, string newName, DateTimeOffset now)
    {
        var copy = values.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = NormalizeName(newName);
        copy.PasswordSaved = false;
        copy.Statistics = new SessionStatistics { Created = now };
        if (ChildNames(copy.Folder).Any(n => string.Equals(n, copy.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Session \"{copy.Name}\" already exists!");
        }
        Sessions.Add(copy);
        return copy;
    }

    /// <summary>
    /// Tree for the list: folders and sessions sorted by name (natural, case-insensitive), with "Folders at top"
    /// putting folders before sessions.
    /// </summary>
    public IReadOnlyList<SessionTreeNode> BuildTree(bool foldersAtTop = true)
    {
        var root = new SessionTreeNode(string.Empty, [], null);
        var folders = new Dictionary<string, SessionTreeNode>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = root };
        foreach (var folder in AllFolders().OrderBy(f => f.Count))
        {
            var parentKey = string.Join('\\', folder.Take(folder.Count - 1));
            var node = new SessionTreeNode(folder[^1], folder.Take(folder.Count - 1).ToList(), null);
            folders[parentKey].Children.Add(node);
            folders[string.Join('\\', folder)] = node;
        }
        foreach (var session in Sessions)
        {
            folders[string.Join('\\', session.Folder)].Children.Add(new SessionTreeNode(session.Name, session.Folder, session));
        }
        Sort(root, foldersAtTop);
        return root.Children;
    }

    /// <summary>
    /// List filter (appendix A §1.4): case-insensitive regex over the text of every column of the sessions; the
    /// parent folders of matching sessions stay visible. An invalid regex = no filter.
    /// </summary>
    public static Func<SessionDefinition, bool> CreateFilter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return _ => true;
        }
        Regex regex;
        try
        {
            regex = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
            return _ => true;
        }
        return s => ColumnTexts(s).Any(t => t.Length > 0 && regex.IsMatch(t));
    }

    /// <summary>Text of the list columns: Session name, Host, Port, User, Version, Last connect, Counter, Comment.</summary>
    public static IEnumerable<string> ColumnTexts(SessionDefinition s)
    {
        yield return s.Name;
        yield return s.Host;
        yield return s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return s.User;
        yield return s.Statistics.ServerVersionFull;
        yield return s.Statistics.LastConnect?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        yield return s.Statistics.ConnectCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return s.Comment;
    }

    public static bool SamePath(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary><paramref name="prefix"/> is <paramref name="path"/> or one of its ancestors.</summary>
    public static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path) =>
        prefix.Count <= path.Count && prefix.Zip(path).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

    private void Reparent(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        static List<string> Replace(IReadOnlyList<string> path, IReadOnlyList<string> from, IReadOnlyList<string> to) =>
            [.. to, .. path.Skip(from.Count)];

        foreach (var session in Sessions.Where(s => IsPrefix(from, s.Folder)))
        {
            session.Folder = Replace(session.Folder, from, to);
        }
        for (var i = 0; i < Folders.Count; i++)
        {
            if (IsPrefix(from, Folders[i]))
            {
                Folders[i] = Replace(Folders[i], from, to);
            }
        }
        if (!Folders.Any(f => SamePath(f, to)))
        {
            Folders.Add([.. to]);
        }
    }

    private static void Sort(SessionTreeNode node, bool foldersAtTop)
    {
        node.Children.Sort((a, b) =>
        {
            if (foldersAtTop && a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        foreach (var child in node.Children)
        {
            Sort(child, foldersAtTop);
        }
    }
}

/// <summary>Session persistence port (<c>sessions.json</c>, section 21.4).</summary>
public interface ISessionStore
{
    SessionCatalog Load();

    void Save(SessionCatalog catalog);
}
