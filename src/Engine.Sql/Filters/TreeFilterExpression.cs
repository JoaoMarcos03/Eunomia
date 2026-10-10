using System.Text.RegularExpressions;

namespace Eunomia.Engine.Sql.Filters;

/// <summary>
/// Expression of the Database filter and the Table filter (section 4.3, ADR-18):
/// <c>"(" + text with ';' replaced by '|' + ")"</c>, case-insensitive, partial match on the name,
/// no trim. An invalid regex hides nothing (the node stays visible) and the message is given once in
/// <see cref="Error"/>. Engine: .NET Regex with <c>IgnoreCase | CultureInvariant</c> and a 50 ms timeout.
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

    /// <summary>Text as it was typed in the box.</summary>
    public string Text { get; }

    /// <summary>Built pattern, or <c>null</c> if the box is empty.</summary>
    public string? Pattern { get; }

    /// <summary>Error message if the regex is invalid (goes to the SQL log once).</summary>
    public string? Error { get; }

    /// <summary>There is an active filter (box with text and a valid regex).</summary>
    public bool IsActive => _regex is not null;

    /// <summary>Whether the name passes the filter. Without a filter, with an invalid regex or on timeout: it passes.</summary>
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
