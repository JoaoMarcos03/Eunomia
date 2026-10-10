using System.Text.RegularExpressions;

namespace Eunomia.App.Sessions;

/// <summary>Nó da lista do Session Manager: folder ou sessão.</summary>
public sealed class SessionTreeNode
{
    public SessionTreeNode(string name, IReadOnlyList<string> folderPath, SessionDefinition? session)
    {
        Name = name;
        FolderPath = folderPath;
        Session = session;
    }

    public string Name { get; }

    /// <summary>Caminho do folder onde o nó está (para um folder, o caminho do pai).</summary>
    public IReadOnlyList<string> FolderPath { get; }

    public SessionDefinition? Session { get; }

    public bool IsFolder => Session is null;

    /// <summary>Caminho completo do folder (só para folders).</summary>
    public IReadOnlyList<string> OwnPath => [.. FolderPath, Name];

    public List<SessionTreeNode> Children { get; } = [];
}

/// <summary>
/// Sessões e folders do Session Manager (FAM-S01, S03; anexo A §1.3–1.5), com as regras do HeidiSQL: nomes únicos
/// entre irmãos sem distinguir maiúsculas (o registo do Windows não distingue, por isso um rename só de
/// maiúsculas é recusado), <c>\</c> passa a <c>-</c>, novos nós chamam-se <c>Unnamed</c>, <c>Unnamed-1</c>…, folders
/// sem ordenação manual. Persistido por <see cref="ISessionStore"/>.
/// </summary>
public sealed class SessionCatalog
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<SessionDefinition> Sessions { get; set; } = [];

    /// <summary>Folders explícitos (também os vazios); os que têm sessões existem implicitamente.</summary>
    public List<List<string>> Folders { get; set; } = [];

    public SessionDefinition? Find(Guid id) => Sessions.FirstOrDefault(s => s.Id == id);

    public SessionDefinition? FindByPath(string path) =>
        Sessions.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Todos os folders (explícitos e implícitos), sem repetidos.</summary>
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

    /// <summary>Nomes dos filhos diretos de um folder (sessões e subfolders).</summary>
    public IEnumerable<string> ChildNames(IReadOnlyList<string> folder) =>
        Sessions.Where(s => SamePath(s.Folder, folder)).Select(s => s.Name)
            .Concat(AllFolders().Where(f => f.Count == folder.Count + 1 && IsPrefix(folder, f)).Select(f => f[^1]));

    /// <summary>Primeiro nome livre: <c>Unnamed</c>, <c>Unnamed-1</c>, <c>Unnamed-2</c>…</summary>
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

    /// <summary>New › Session (gravada logo, como no HeidiSQL).</summary>
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

    /// <summary>Nome válido para gravar: <c>\</c> passa a <c>-</c> (issue #682 do HeidiSQL), espaços aparados.</summary>
    public static string NormalizeName(string name) => name.Replace('\\', '-').Trim();

    /// <summary>Rename de uma sessão. Devolve o erro a mostrar, ou <c>null</c> se correu bem.</summary>
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

    /// <summary>Rename de um folder: muda o caminho de todas as sessões e subfolders.</summary>
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
    /// Drag &amp; drop: move uma sessão para um folder. Recusa largar no folder onde já está e nomes repetidos.
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

    /// <summary>Move um folder; recusa o próprio pai, ele mesmo e os seus descendentes.</summary>
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

    /// <summary>Delete de uma sessão.</summary>
    public void DeleteSession(SessionDefinition session) => Sessions.Remove(session);

    /// <summary>Delete de um folder: apaga todas as sessões lá dentro (devolvidas para limpar as credenciais).</summary>
    public IReadOnlyList<SessionDefinition> DeleteFolder(IReadOnlyList<string> path)
    {
        var removed = Sessions.Where(s => IsPrefix(path, s.Folder)).ToList();
        Sessions.RemoveAll(s => removed.Contains(s));
        Folders.RemoveAll(f => IsPrefix(path, f));
        return removed;
    }

    /// <summary>Duplicate / save as (F12): cópia dos valores com outro nome e outro id, no mesmo folder.</summary>
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
    /// Árvore para a lista: folders e sessões ordenados pelo nome (natural, sem maiúsculas), com "Folders at top"
    /// a pôr os folders antes das sessões.
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
    /// Filtro da lista (anexo A §1.4): regex sem maiúsculas sobre o texto de todas as colunas das sessões; os
    /// folders pais das sessões que passam ficam visíveis. Regex inválida = sem filtro.
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

    /// <summary>Texto das colunas da lista: Session name, Host, Port, User, Version, Last connect, Counter, Comment.</summary>
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

    /// <summary><paramref name="prefix"/> é <paramref name="path"/> ou um antepassado dele.</summary>
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

/// <summary>Porta de persistência das sessões (<c>sessions.json</c>, secção 21.4).</summary>
public interface ISessionStore
{
    SessionCatalog Load();

    void Save(SessionCatalog catalog);
}
