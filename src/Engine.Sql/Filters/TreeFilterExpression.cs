using System.Text.RegularExpressions;

namespace Eunomia.Engine.Sql.Filters;

/// <summary>
/// Expressão do Database filter e do Table filter (secção 4.3, ADR-18):
/// <c>"(" + texto com ';' trocado por '|' + ")"</c>, case-insensitive, correspondência parcial ao nome,
/// sem trim. Uma regex inválida não esconde nada (o nó fica visível) e a mensagem é dada uma vez em
/// <see cref="Error"/>. Motor: Regex .NET com <c>IgnoreCase | CultureInvariant</c> e timeout de 50 ms.
/// </summary>
public sealed class TreeFilterExpression
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    public static TreeFilterExpression Empty { get; } = new(string.Empty);

    private readonly Regex? _regex;

    public TreeFilterExpression(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        if (text.Length == 0)
        {
            return;
        }

        Pattern = "(" + text.Replace(';', '|') + ")";
        try
        {
            _regex = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Texto tal como foi escrito na caixa.</summary>
    public string Text { get; }

    /// <summary>Padrão construído, ou <c>null</c> se a caixa estiver vazia.</summary>
    public string? Pattern { get; }

    /// <summary>Mensagem de erro se a regex for inválida (vai uma vez para o SQL log).</summary>
    public string? Error { get; }

    /// <summary>Há filtro ativo (caixa com texto e regex válida).</summary>
    public bool IsActive => _regex is not null;

    /// <summary>Indica se o nome passa no filtro. Sem filtro, regex inválida ou timeout: passa.</summary>
    public bool IsMatch(string name)
    {
        if (_regex is null)
        {
            return true;
        }
        try
        {
            return _regex.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
