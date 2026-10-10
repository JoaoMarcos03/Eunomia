namespace Eunomia.Engine.Sql.Splitting;

/// <summary>Splitter resume point (section 13.3): absolute position in characters, line and the delimiter in effect.</summary>
public readonly record struct SplitterPosition(long Offset, long Line, string Delimiter)
{
    public static SplitterPosition Start(string delimiter = ";") => new(0, 1, delimiter);
}

/// <summary>A statement split in streaming, with absolute positions (in characters) in the whole text.</summary>
/// <param name="Text">Statement without the delimiter or the surrounding whitespace.</param>
/// <param name="Start">Position of the statement's first character.</param>
/// <param name="End">Position after the statement's last character.</param>
/// <param name="Line">Line (from 1) of the first character.</param>
/// <param name="Delimiter">Delimiter in effect when the statement ended (for the SQL log).</param>
/// <param name="Next">Where to resume after this statement: after the delimiter, or after the <c>DELIMITER</c> line that ended it.</param>
public sealed record StreamedStatement(string Text, long Start, long End, long Line, string Delimiter, SplitterPosition Next);

/// <summary>
/// Splitter with state across buffers (section 13.2, Run SQL file): the same rules as <see cref="SqlSplitter"/>, but the
/// text arrives in pieces and quotes, comments and the <c>DELIMITER</c> in effect carry over to the next piece
/// (HeidiSQL loses the <c>DELIMITER</c> at the 20 MB boundary, appendix B §4.3). Only the unfinished statement stays in memory.
/// </summary>
public sealed class StreamingSqlSplitter
{
    private enum State
    {
        Normal,
        Quoted,
        LineComment,
        BlockComment,
    }

    private const string DelimiterKeyword = "DELIMITER";

    private char[] _buffer = new char[4096];
    private int _length;

    // Absolute position of _buffer[0].
    private long _baseOffset;
    private int _scan;
    private int _statementStart;
    private long _statementLine;
    private long _line;
    private State _state;
    private char _quote;
    private bool _atLineStart;
    private bool _completed;

    public StreamingSqlSplitter(string delimiter = ";")
        : this(SplitterPosition.Start(delimiter))
    {
    }

    /// <summary>
    /// Starts at a resume point (<see cref="StreamedStatement.Next"/>); the text passed to <see cref="Append"/> starts
    /// at that position.
    /// </summary>
    public StreamingSqlSplitter(SplitterPosition start)
    {
        if (!SqlSplitter.IsValidDelimiter(start.Delimiter))
        {
            throw new ArgumentException($"Invalid delimiter: \"{start.Delimiter}\".", nameof(start));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(start.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(start.Line, 1);
        Delimiter = start.Delimiter;
        _baseOffset = start.Offset;
        _line = start.Line;
        _statementLine = start.Line;
        // A resume point comes after a delimiter or before the '\n' of a DELIMITER line, never at the start of
        // a line.
        _atLineStart = start.Offset == 0;
    }

    /// <summary>Delimiter in effect.</summary>
    public string Delimiter { get; private set; }

    /// <summary>Characters kept for the unfinished statement (to cap it at <c>max_allowed_packet</c>).</summary>
    public int PendingLength => _length - _statementStart;

    /// <summary>Adds a piece of text and returns the statements that became complete.</summary>
    public void Append(ReadOnlySpan<char> chunk, ICollection<StreamedStatement> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_completed, this);
        Compact();
        EnsureCapacity(_length + chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
        Scan(final: false, output);
    }

    /// <summary>End of the text: returns the last statement, even without a delimiter.</summary>
    public void Complete(ICollection<StreamedStatement> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_completed)
        {
            return;
        }
        Scan(final: true, output);
        Emit(_statementStart, _length, output, new SplitterPosition(_baseOffset + _length, _line, Delimiter));
        _completed = true;
    }

    private void Scan(bool final, ICollection<StreamedStatement> output)
    {
        var buffer = _buffer;
        var length = _length;
        var pos = _scan;
        while (pos < length)
        {
            var c = buffer[pos];
            switch (_state)
            {
                case State.Quoted:
                    // Inside strings (most of a dump) skips to the next character that matters.
                    var rest = buffer.AsSpan(pos, length - pos);
                    var hit = _quote == '`' ? rest.IndexOfAny('`', '\n') : rest.IndexOfAny(_quote, '\\', '\n');
                    if (hit < 0)
                    {
                        pos = length;
                        continue;
                    }
                    pos += hit;
                    c = buffer[pos];
                    if (c == '\n')
                    {
                        _line++;
                    }
                    else if (c == '\\' && _quote != '`')
                    {
                        if (pos + 1 >= length)
                        {
                            if (!final)
                            {
                                goto Wait;
                            }
                            pos++;
                            continue;
                        }
                        if (buffer[pos + 1] == '\n')
                        {
                            _line++;
                        }
                        pos += 2;
                        continue;
                    }
                    else if (c == _quote)
                    {
                        if (pos + 1 >= length)
                        {
                            if (!final)
                            {
                                goto Wait;
                            }
                        }
                        else if (buffer[pos + 1] == _quote)
                        {
                            pos += 2;
                            continue;
                        }
                        _state = State.Normal;
                    }
                    pos++;
                    continue;

                case State.LineComment:
                    var newline = buffer.AsSpan(pos, length - pos).IndexOf('\n');
                    if (newline < 0)
                    {
                        pos = length;
                        continue;
                    }
                    // The '\n' is handled in the normal state (counts the line and marks the start of the line).
                    pos += newline;
                    _state = State.Normal;
                    continue;

                case State.BlockComment:
                    if (c == '\n')
                    {
                        _line++;
                    }
                    else if (c == '*')
                    {
                        if (pos + 1 >= length)
                        {
                            if (!final)
                            {
                                goto Wait;
                            }
                        }
                        else if (buffer[pos + 1] == '/')
                        {
                            _state = State.Normal;
                            pos += 2;
                            continue;
                        }
                    }
                    pos++;
                    continue;
            }

            if (_atLineStart && c is not (' ' or '\t' or '\r' or '\n'))
            {
                switch (TryReadDelimiterCommand(buffer.AsSpan(pos, length - pos), final, out var newDelimiter, out var commandLength))
                {
                    case CommandMatch.NeedMore:
                        goto Wait;
                    case CommandMatch.Yes:
                        // The DELIMITER line is not sent; the '\n' after it counts the line. Resuming comes after it.
                        Emit(_statementStart, pos, output, new SplitterPosition(_baseOffset + pos + commandLength, _line, newDelimiter));
                        Delimiter = newDelimiter;
                        pos += commandLength;
                        _statementStart = pos;
                        _statementLine = _line;
                        continue;
                }
            }
            if (c == '\n')
            {
                _line++;
                _atLineStart = true;
                pos++;
                continue;
            }
            if (_atLineStart && c is ' ' or '\t' or '\r')
            {
                pos++;
                continue;
            }
            _atLineStart = false;
            if (c is '\'' or '"' or '`')
            {
                _state = State.Quoted;
                _quote = c;
                pos++;
                continue;
            }
            if (c == '#')
            {
                _state = State.LineComment;
                pos++;
                continue;
            }
            if (c == '-' && (pos + 1 >= length ? !final : buffer[pos + 1] == '-'))
            {
                if (pos + 2 >= length)
                {
                    if (!final)
                    {
                        goto Wait;
                    }
                    // "--" at the end of the text is a comment, as in SqlSplitter.
                    _state = State.LineComment;
                    pos += 2;
                    continue;
                }
                if (char.IsWhiteSpace(buffer[pos + 2]))
                {
                    _state = State.LineComment;
                    pos += 2;
                    continue;
                }
            }
            if (c == '/')
            {
                if (pos + 1 >= length && !final)
                {
                    goto Wait;
                }
                if (pos + 1 < length && buffer[pos + 1] == '*')
                {
                    _state = State.BlockComment;
                    pos += 2;
                    continue;
                }
            }
            var delimiter = Delimiter;
            if (c == delimiter[0])
            {
                var available = Math.Min(delimiter.Length, length - pos);
                if (buffer.AsSpan(pos, available).SequenceEqual(delimiter.AsSpan(0, available)))
                {
                    if (available < delimiter.Length)
                    {
                        if (!final)
                        {
                            goto Wait;
                        }
                    }
                    else
                    {
                        Emit(_statementStart, pos, output, new SplitterPosition(_baseOffset + pos + delimiter.Length, _line, delimiter));
                        pos += delimiter.Length;
                        _statementStart = pos;
                        _statementLine = _line;
                        continue;
                    }
                }
            }
            pos++;
        }

    Wait:
        _scan = pos;
    }

    private void Emit(int start, int end, ICollection<StreamedStatement> output, SplitterPosition next)
    {
        var line = _statementLine;
        var s = start;
        while (s < end && char.IsWhiteSpace(_buffer[s]))
        {
            if (_buffer[s] == '\n')
            {
                line++;
            }
            s++;
        }
        var e = end;
        while (e > s && char.IsWhiteSpace(_buffer[e - 1]))
        {
            e--;
        }
        if (e <= s || SqlSplitter.IsOnlyComments(_buffer.AsSpan(s, e - s)))
        {
            return;
        }
        output.Add(new StreamedStatement(new string(_buffer, s, e - s), _baseOffset + s, _baseOffset + e, line, Delimiter, next));
    }

    private enum CommandMatch
    {
        No,
        Yes,
        NeedMore,
    }

    /// <summary>
    /// <c>DELIMITER xyz</c> at the start of the line (the same rules as <see cref="SqlSplitter"/>); without the end of the line
    /// in the buffer, waits for more text, but only if what has arrived could still be a command.
    /// </summary>
    private static CommandMatch TryReadDelimiterCommand(ReadOnlySpan<char> text, bool final, out string delimiter, out int length)
    {
        delimiter = string.Empty;
        length = 0;
        var prefix = Math.Min(text.Length, DelimiterKeyword.Length);
        if (!text[..prefix].Equals(DelimiterKeyword.AsSpan(0, prefix), StringComparison.OrdinalIgnoreCase))
        {
            return CommandMatch.No;
        }
        if (text.Length > DelimiterKeyword.Length && text[DelimiterKeyword.Length] is not (' ' or '\t'))
        {
            return CommandMatch.No;
        }
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0)
        {
            if (!final)
            {
                return CommandMatch.NeedMore;
            }
            lineEnd = text.Length;
        }
        var line = text[..lineEnd];
        if (line.Length <= DelimiterKeyword.Length || line[DelimiterKeyword.Length] is not (' ' or '\t'))
        {
            return CommandMatch.No;
        }
        var p = DelimiterKeyword.Length;
        while (p < line.Length && line[p] is ' ' or '\t')
        {
            p++;
        }
        var valueStart = p;
        while (p < line.Length && !char.IsWhiteSpace(line[p]))
        {
            p++;
        }
        var value = line[valueStart..p].ToString();
        if (!SqlSplitter.IsValidDelimiter(value))
        {
            return CommandMatch.No;
        }
        delimiter = value;
        length = lineEnd;
        return CommandMatch.Yes;
    }

    private void Compact()
    {
        if (_statementStart == 0)
        {
            return;
        }
        var keep = _length - _statementStart;
        Array.Copy(_buffer, _statementStart, _buffer, 0, keep);
        _baseOffset += _statementStart;
        _scan -= _statementStart;
        _length = keep;
        _statementStart = 0;
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _buffer.Length)
        {
            return;
        }
        var size = Math.Max(capacity, (int)Math.Min(Array.MaxLength, _buffer.Length * 2L));
        Array.Resize(ref _buffer, size);
    }
}
