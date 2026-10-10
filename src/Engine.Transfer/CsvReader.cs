using System.Text;

namespace Eunomia.Engine.Transfer;

/// <summary>
/// CSV import options with HeidiSQL's defaults (FAM-E07; appendix B §3.1): fields terminated by <c>;</c>,
/// (optionally) enclosed by <c>"</c>, escape <c>"</c>, lines terminated by <c>\r\n</c>, skip 1 line.
/// </summary>
public sealed record CsvOptions
{
    public string FieldTerminator { get; init; } = ";";

    /// <summary>Empty: fields never come quoted.</summary>
    public string Encloser { get; init; } = "\"";

    /// <summary>Only for <c>LOAD DATA</c> (<c>OPTIONALLY ENCLOSED BY</c>); the client parser accepts both cases.</summary>
    public bool OptionallyEnclosed { get; init; } = true;

    public string Escaper { get; init; } = "\"";

    public string LineTerminator { get; init; } = "\r\n";

    public int IgnoreLines { get; init; } = 1;

    /// <summary>
    /// Dialog values with backslash sequences (<c>\t</c>, <c>\r</c>, <c>\n</c>, <c>\\</c>, <c>\0</c>),
    /// as HeidiSQL interprets them.
    /// </summary>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                char? mapped = next switch
                {
                    't' => '\t',
                    'r' => '\r',
                    'n' => '\n',
                    '0' => '\0',
                    '\\' => '\\',
                    _ => null,
                };
                if (mapped is { } m)
                {
                    sb.Append(m);
                    i++;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Streaming CSV parser for client mode (section 13.2): RFC 4180 with HeidiSQL's options, without reading the
/// whole file. Differences from HeidiSQL (appendix B §3.3): a doubled encloser (<c>""</c>) becomes a single one, an
/// unquoted field equal to <c>\N</c> or <c>NULL</c> is NULL, the escape works when it differs from the encloser
/// (<c>\</c> with MySQL's sequences), and a line end inside quotes is part of the value. Empty lines are skipped.
/// </summary>
public sealed class CsvReader
{
    private readonly TextReader _reader;
    private readonly string _field;
    private readonly string _line;
    private readonly string _encloser;
    private readonly char? _escaper;
    private readonly List<string?> _values = [];
    private readonly StringBuilder _value = new();
    private char[] _buffer;
    private int _start;
    private int _length;
    private bool _eof;
    private int _toSkip;

    public CsvReader(TextReader reader, CsvOptions options, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.FieldTerminator) || string.IsNullOrEmpty(options.LineTerminator))
        {
            throw new ArgumentException("Field and line terminators cannot be empty.", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 16);
        _reader = reader;
        _field = options.FieldTerminator;
        _line = options.LineTerminator;
        _encloser = options.Encloser;
        // An escape equal to the encloser (HeidiSQL's default) is RFC 4180's "", already handled inside the quotes.
        _escaper = options.Escaper.Length == 1 && options.Escaper != options.Encloser ? options.Escaper[0] : null;
        _toSkip = Math.Max(0, options.IgnoreLines);
        _buffer = new char[bufferSize];
    }

    /// <summary>Records read so far, including those skipped at the start (the CSV import checkpoint).</summary>
    public long RecordsRead { get; private set; }

    /// <summary>Characters consumed (progress when the stream has no position).</summary>
    public long CharactersRead { get; private set; }

    /// <summary>Next record, or <c>null</c> at the end of the file. NULL is <c>null</c> in the list.</summary>
    public string?[]? Read()
    {
        while (true)
        {
            var record = ReadRecord();
            if (record is null)
            {
                return null;
            }
            RecordsRead++;
            if (_toSkip > 0)
            {
                _toSkip--;
                continue;
            }
            if (record.Length == 1 && record[0] is { Length: 0 })
            {
                continue;
            }
            return record;
        }
    }

    /// <summary>Skips records without returning them (resuming from the checkpoint).</summary>
    public void Skip(long records)
    {
        for (var i = 0L; i < records && ReadRecord() is not null; i++)
        {
            RecordsRead++;
            if (_toSkip > 0)
            {
                _toSkip--;
            }
        }
    }

    private string?[]? ReadRecord()
    {
        while (true)
        {
            var result = TryParse(out var consumed);
            if (result is not null)
            {
                _start += consumed;
                CharactersRead += consumed;
                return result;
            }
            if (_eof)
            {
                if (_length - _start == 0)
                {
                    return null;
                }
                // No terminator at the end: TryParse at EOF always closes the record; this is never reached.
                throw new InvalidOperationException("CSV parser did not consume the end of the input.");
            }
            Fill();
        }
    }

    /// <summary>Reads a record starting at <see cref="_start"/>; <c>null</c> if the buffer ends first (and it is not the end).</summary>
    private string?[]? TryParse(out int consumed)
    {
        consumed = 0;
        if (_length - _start == 0 && _eof)
        {
            return null;
        }
        var span = _buffer.AsSpan(_start, _length - _start);
        _values.Clear();
        var pos = 0;
        while (true)
        {
            _value.Clear();
            var enclosed = false;
            var isNullMarker = false;
            var partial = false;
            if (_encloser.Length > 0 && StartsWith(span, pos, _encloser, out partial))
            {
                enclosed = true;
                pos += _encloser.Length;
                while (true)
                {
                    if (pos >= span.Length)
                    {
                        if (!_eof)
                        {
                            return null;
                        }
                        // Quotes left open at the end of the file: the rest is the value.
                        break;
                    }
                    if (StartsWith(span, pos, _encloser, out partial))
                    {
                        if (StartsWith(span, pos + _encloser.Length, _encloser, out var partialDouble))
                        {
                            _value.Append(_encloser);
                            pos += 2 * _encloser.Length;
                            continue;
                        }
                        if (partialDouble && !_eof)
                        {
                            return null;
                        }
                        pos += _encloser.Length;
                        break;
                    }
                    if (partial && !_eof)
                    {
                        return null;
                    }
                    if (_escaper is { } esc && span[pos] == esc)
                    {
                        if (pos + 1 >= span.Length)
                        {
                            if (!_eof)
                            {
                                return null;
                            }
                            _value.Append(esc);
                            pos++;
                            continue;
                        }
                        AppendEscaped(span[pos + 1]);
                        pos += 2;
                        continue;
                    }
                    _value.Append(span[pos]);
                    pos++;
                }
            }
            else if (partial && !_eof)
            {
                return null;
            }

            // Up to the field or line terminator (after the quotes, whatever comes is appended to the value).
            var rawStart = _value.Length;
            while (true)
            {
                if (pos >= span.Length)
                {
                    if (!_eof)
                    {
                        return null;
                    }
                    AddValue(enclosed, isNullMarker);
                    consumed = pos;
                    return [.. _values];
                }
                if (StartsWith(span, pos, _field, out var partialField))
                {
                    AddValue(enclosed, isNullMarker);
                    pos += _field.Length;
                    break;
                }
                if (StartsWith(span, pos, _line, out var partialLine))
                {
                    AddValue(enclosed, isNullMarker);
                    consumed = pos + _line.Length;
                    return [.. _values];
                }
                if ((partialField || partialLine) && !_eof)
                {
                    return null;
                }
                if (_escaper is { } esc && span[pos] == esc)
                {
                    if (pos + 1 >= span.Length)
                    {
                        if (!_eof)
                        {
                            return null;
                        }
                        _value.Append(esc);
                        pos++;
                        continue;
                    }
                    if (esc == '\\' && span[pos + 1] == 'N' && _value.Length == rawStart && !enclosed)
                    {
                        isNullMarker = true;
                    }
                    AppendEscaped(span[pos + 1]);
                    pos += 2;
                    continue;
                }
                _value.Append(span[pos]);
                pos++;
            }
        }
    }

    private void AddValue(bool enclosed, bool isNullMarker)
    {
        if (!enclosed)
        {
            // \N (with or without a configured escape) and the unquoted word NULL are NULL, as in LOAD DATA.
            var raw = _value.ToString();
            if ((isNullMarker && raw == "N") || raw == "\\N" || raw == "NULL")
            {
                _values.Add(null);
                return;
            }
            _values.Add(raw);
            return;
        }
        _values.Add(_value.ToString());
    }

    private void AppendEscaped(char c)
    {
        if (_escaper != '\\')
        {
            _value.Append(c);
            return;
        }
        _value.Append(c switch
        {
            '0' => '\0',
            'b' => '\b',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'Z' => '\x1A',
            _ => c,
        });
    }

    /// <summary>
    /// The text at <paramref name="pos"/> starts with <paramref name="token"/>; <paramref name="partial"/> says whether the
    /// buffer ends in the middle of a possible match (more must be read to decide).
    /// </summary>
    private static bool StartsWith(ReadOnlySpan<char> span, int pos, string token, out bool partial)
    {
        partial = false;
        var available = span.Length - pos;
        if (available <= 0)
        {
            partial = true;
            return false;
        }
        if (available >= token.Length)
        {
            return span.Slice(pos, token.Length).SequenceEqual(token);
        }
        partial = span[pos..].SequenceEqual(token.AsSpan(0, available));
        return false;
    }

    private void Fill()
    {
        if (_start > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, _length - _start);
            _length -= _start;
            _start = 0;
        }
        if (_length == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }
        var read = _reader.Read(_buffer, _length, _buffer.Length - _length);
        if (read == 0)
        {
            _eof = true;
        }
        _length += read;
    }
}
