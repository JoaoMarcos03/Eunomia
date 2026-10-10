using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Completion;

namespace Eunomia.App.Query;

/// <summary>
/// Autocomplete A (secção 16.1; anexo C §4.2; FAM-Q05) sobre a cache de metadata da sessão: <c>@@SESSION.</c> /
/// <c>@@GLOBAL.</c> lista as variáveis com o valor; <c>alias.</c> e <c>db.tbl.</c> as colunas (chave, tipo em
/// minúsculas, comentário); <c>db.</c> os objetos da base; sem qualificador, as colunas da tabela selecionada na árvore,
/// as bases, as tabelas, views e rotinas da base atual, as funções e as keywords. Os identificadores entram entre crases
/// quando é preciso, ou sempre que a palavra começa por uma crase. Os erros de metadata dão uma lista mais curta. Sem
/// sessão ligada, só funções e keywords.
/// </summary>
public sealed class QueryCompletion(OpenSession? session)
{
    /// <summary>Sessão ligada (os passos com metadata só correm com ela).</summary>
    private OpenSession Session => session ?? throw new InvalidOperationException("No session.");

    /// <summary>Linguagem da sessão (nomes, funções e palavras); sem sessão, a de omissão.</summary>
    private ISqlLanguage Language => session?.Language ?? SqlLanguages.Default;

    /// <summary>Sugestões para o cursor em <paramref name="caret"/>; <c>null</c> dentro de strings e comentários.</summary>
    /// <param name="selectedTable">Tabela ou view selecionada na árvore (colunas sem qualificador).</param>
    public async Task<CompletionProposal?> ProposeAsync(string text, int caret, string delimiter, DbObjectRef? selectedTable, CancellationToken cancellationToken)
    {
        var context = CompletionContext.Analyze(text, caret, delimiter);
        var items = new List<CompletionItem>();
        if (session is null)
        {
            if (context.Scope == CompletionScope.General)
            {
                AddFunctionsAndKeywords(items);
            }
            return context.Scope == CompletionScope.Suppressed ? null : new CompletionProposal(context.ReplaceStart, items);
        }
        switch (context.Scope)
        {
            case CompletionScope.Suppressed:
                return null;
            case CompletionScope.Variables:
                var variables = await TryAsync(() => Session.Host.RunMetadataAsync(
                    (h, ct) => h.Metadata.GetVariablesAsync(!context.GlobalVariables, ct), cancellationToken)).ConfigureAwait(false) ?? [];
                items.AddRange(variables.Select(v => new CompletionItem(v.Name, CompletionKind.Variable) { Detail = v.Value }));
                break;
            case CompletionScope.Qualified:
                await AddQualifiedAsync(context, items, cancellationToken).ConfigureAwait(false);
                break;
            default:
                if (selectedTable is { Kind: DbObjectKind.Table or DbObjectKind.View })
                {
                    await AddColumnsAsync(selectedTable, context, items, cancellationToken).ConfigureAwait(false);
                }
                var databases = await TryAsync(() => Session.Metadata.GetDatabasesAsync(cancellationToken)).ConfigureAwait(false) ?? [];
                items.AddRange(databases.Select(d => Identifier(d, CompletionKind.Database, context)));
                if (Session.Host.CurrentDatabase is { } current)
                {
                    await AddObjectsAsync(current, context, items, cancellationToken).ConfigureAwait(false);
                }
                AddFunctionsAndKeywords(items);
                break;
        }
        return new CompletionProposal(context.ReplaceStart, items);
    }

    private void AddFunctionsAndKeywords(List<CompletionItem> items)
    {
        var language = Language;
        items.AddRange(language.Functions.Order(StringComparer.OrdinalIgnoreCase).Select(f => new CompletionItem(f, CompletionKind.Function)));
        items.AddRange(language.Keywords.Order(StringComparer.OrdinalIgnoreCase).Select(k => new CompletionItem(k, CompletionKind.Keyword)));
        items.AddRange(language.DataTypes.Order(StringComparer.OrdinalIgnoreCase).Select(t => new CompletionItem(t, CompletionKind.DataType)));
    }

    /// <summary><c>alias.</c>, <c>tbl.</c> (da instrução ou da base atual), <c>db.</c> e <c>db.tbl.</c>.</summary>
    private async Task AddQualifiedAsync(CompletionContext context, List<CompletionItem> items, CancellationToken cancellationToken)
    {
        var current = Session.Host.CurrentDatabase;
        if (context.Qualifiers is [var database, var table])
        {
            if (await FindObjectAsync(database, table, cancellationToken).ConfigureAwait(false) is { } found)
            {
                await AddColumnsAsync(found, context, items, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        var name = context.Qualifiers[0];
        var reference = context.Tables.FirstOrDefault(t => string.Equals(t.Alias, name, StringComparison.OrdinalIgnoreCase))
                        ?? context.Tables.FirstOrDefault(t => t.Alias is null && string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase));
        if (reference is not null && (reference.Database ?? current) is { } db
            && await FindObjectAsync(db, reference.Table, cancellationToken).ConfigureAwait(false) is { } referenced)
        {
            await AddColumnsAsync(referenced, context, items, cancellationToken).ConfigureAwait(false);
            return;
        }
        var databases = await TryAsync(() => Session.Metadata.GetDatabasesAsync(cancellationToken)).ConfigureAwait(false) ?? [];
        if (databases.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) is { } matched)
        {
            await AddObjectsAsync(matched, context, items, cancellationToken).ConfigureAwait(false);
        }
        else if (current is not null && await FindObjectAsync(current, name, cancellationToken).ConfigureAwait(false) is { } inCurrent)
        {
            await AddColumnsAsync(inCurrent, context, items, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DbObjectRef?> FindObjectAsync(string database, string name, CancellationToken cancellationToken)
    {
        var objects = await TryAsync(() => Session.Metadata.GetObjectsAsync(database, cancellationToken)).ConfigureAwait(false);
        return objects?.Objects.Select(o => o.Ref)
            .Where(r => r.Kind is DbObjectKind.Table or DbObjectKind.View)
            .OrderBy(r => string.Equals(r.Name, name, StringComparison.Ordinal) ? 0 : 1)
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tabelas, views e rotinas de uma base (triggers e eventos não entram).</summary>
    private async Task AddObjectsAsync(string database, CompletionContext context, List<CompletionItem> items, CancellationToken cancellationToken)
    {
        var objects = await TryAsync(() => Session.Metadata.GetObjectsAsync(database, cancellationToken)).ConfigureAwait(false);
        foreach (var o in objects?.Objects ?? [])
        {
            var kind = o.Ref.Kind switch
            {
                DbObjectKind.Table => CompletionKind.Table,
                DbObjectKind.View => CompletionKind.View,
                DbObjectKind.Procedure or DbObjectKind.Function => CompletionKind.Routine,
                _ => (CompletionKind?)null,
            };
            if (kind is { } k)
            {
                items.Add(Identifier(o.Ref.Name, k, context) with
                {
                    Detail = k == CompletionKind.Routine ? o.Ref.Kind.ToString().ToLowerInvariant() : null,
                });
            }
        }
    }

    private async Task AddColumnsAsync(DbObjectRef table, CompletionContext context, List<CompletionItem> items, CancellationToken cancellationToken)
    {
        var definition = await TryAsync(() => Session.Metadata.GetTableAsync(table, cancellationToken)).ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }
        var keys = definition.Indexes.Where(i => i.Kind == IndexKind.Primary).SelectMany(i => i.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items.AddRange(definition.Columns.Select(c => Identifier(c.Name, CompletionKind.Column, context) with
        {
            Detail = c.NativeType.ToLowerInvariant(),
            Description = string.IsNullOrEmpty(c.Comment) ? null : c.Comment,
            IsKey = keys.Contains(c.Name),
        }));
    }

    private CompletionItem Identifier(string name, CompletionKind kind, CompletionContext context)
    {
        var insert = context.Quoted ? Language.QuoteIdentifier(name) : Language.EditorIdentifier(name);
        return new CompletionItem(name, kind) { InsertText = string.Equals(insert, name, StringComparison.Ordinal) ? null : insert };
    }

    /// <summary>Erros de metadata não interrompem o autocomplete (o cancelamento sim).</summary>
    private static async Task<T?> TryAsync<T>(Func<Task<T>> load)
        where T : class
    {
        try
        {
            return await load().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }
    }
}
