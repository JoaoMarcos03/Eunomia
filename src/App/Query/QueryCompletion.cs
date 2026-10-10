using Eunomia.App.Sessions;
using Eunomia.Drivers.Abstractions;
using Eunomia.Engine.Sql;
using Eunomia.Engine.Sql.Completion;

namespace Eunomia.App.Query;

/// <summary>
/// Autocomplete A (section 16.1; appendix C §4.2; FAM-Q05) on top of the session's metadata cache: <c>@@SESSION.</c> /
/// <c>@@GLOBAL.</c> lists the variables with their values; <c>alias.</c> and <c>db.tbl.</c> the columns (key, type in
/// lower case, comment); <c>db.</c> the objects of the database; without a qualifier, the columns of the table selected
/// in the tree, the databases, the tables, views and routines of the current database, the functions and the keywords.
/// Identifiers are inserted in backticks when needed, or whenever the word starts with a backtick. Metadata errors
/// give a shorter list. Without a connected session, only functions and keywords.
/// </summary>
public sealed class QueryCompletion(OpenSession? session)
{
    /// <summary>Connected session (the steps that need metadata only run with it).</summary>
    private OpenSession Session => session ?? throw new InvalidOperationException("No session.");

    /// <summary>The session's language (names, functions and keywords); without a session, the default one.</summary>
    private ISqlLanguage Language => session?.Language ?? SqlLanguages.Default;

    /// <summary>Suggestions for the caret at <paramref name="caret"/>; <c>null</c> inside strings and comments.</summary>
    /// <param name="selectedTable">Table or view selected in the tree (columns without a qualifier).</param>
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

    /// <summary><c>alias.</c>, <c>tbl.</c> (from the statement or the current database), <c>db.</c> and <c>db.tbl.</c>.</summary>
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

    /// <summary>Tables, views and routines of a database (triggers and events are left out).</summary>
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

    /// <summary>Metadata errors do not interrupt the autocomplete (cancellation does).</summary>
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
