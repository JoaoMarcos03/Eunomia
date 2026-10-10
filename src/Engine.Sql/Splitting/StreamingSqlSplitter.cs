namespace Eunomia.Engine.Sql.Splitting;

/// <summary>Ponto de retoma do splitter (secção 13.3): posição absoluta em caracteres, linha e delimitador em vigor.</summary>
public readonly record struct SplitterPosition(long Offset, long Line, string Delimiter)
{
    public static SplitterPosition Start(string delimiter = ";") => new(0, 1, delimiter);
}

/// <summary>Uma instrução separada em streaming, com posições absolutas (em caracteres) no texto todo.</summary>
/// <param name="Text">Instrução sem o delimitador nem os espaços à volta.</param>
/// <param name="Start">Posição do primeiro carácter da instrução.</param>
/// <param name="End">Posição a seguir ao último carácter da instrução.</param>
/// <param name="Line">Linha (a partir de 1) do primeiro carácter.</param>
/// <param name="Delimiter">Delimitador em vigor quando a instrução acabou (para o SQL log).</param>
/// <param name="Next">Onde retomar depois desta instrução: a seguir ao delimitador, ou à linha <c>DELIMITER</c> que a acabou.</param>
public sealed record StreamedStatement(string Text, long Start, long End, long Line, string Delimiter, SplitterPosition Next);

/// <summary>
/// Splitter com estado entre buffers (secção 13.2, Run SQL file): as mesmas regras do <see cref="SqlSplitter"/>, mas o
/// texto chega aos pedaços e as aspas, os comentários e o <c>DELIMITER</c> em vigor continuam no pedaço seguinte (o
/// HeidiSQL perde o <c>DELIMITER</c> na fronteira dos 20 MB, anexo B §4.3). Só a instrução por acabar fica em memória.
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

    // Posição absoluta de _buffer[0].
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
    /// Começa num ponto de retoma (<see cref="StreamedStatement.Next"/>); o texto passado a <see cref="Append"/> começa
    /// nessa posição.
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
        // Um ponto de retoma vem a seguir a um delimitador ou antes do '\n' de uma linha DELIMITER, nunca no início de
        // uma linha.
        _atLineStart = start.Offset == 0;
    }

    /// <summary>Delimitador em vigor.</summary>
    public string Delimiter { get; private set; }

    /// <summary>Caracteres guardados da instrução por acabar (para limitar ao <c>max_allowed_packet</c>).</summary>
    public int PendingLength => _length - _statementStart;

    /// <summary>Junta um pedaço de texto e devolve as instruções que ficaram completas.</summary>
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

    /// <summary>Fim do texto: devolve a última instrução, mesmo sem delimitador.</summary>
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
                    // Dentro de strings (a maior parte de um dump) salta até ao próximo carácter que importa.
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
                    // O '\n' é tratado no estado normal (conta a linha e marca o início da linha).
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
                        // A linha do DELIMITER não é enviada; o '\n' a seguir conta a linha. A retoma é depois dela.
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
                    // "--" no fim do texto é um comentário, como no SqlSplitter.
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
    /// <c>DELIMITER xyz</c> no início da linha (as mesmas regras do <see cref="SqlSplitter"/>); sem o fim da linha no
    /// buffer, espera por mais texto, mas só se o que já chegou ainda pode ser um comando.
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
