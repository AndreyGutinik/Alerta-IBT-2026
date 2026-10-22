using System.Text;

namespace AlertaIBT.Core.Firebase;

/// <summary>One event of a Server-Sent Events stream.</summary>
/// <param name="Name">The <c>event:</c> field; <c>message</c> when the event had none.</param>
/// <param name="Data">The <c>data:</c> lines joined with a line feed.</param>
public sealed record SseEvent(string Name, string Data);

/// <summary>
/// Turns the text of a Server-Sent Events stream into events, however it is cut into chunks: a line, a CR LF pair or a
/// multi-byte letter may be split across two chunks. Lines end with LF, CR or CR LF; a blank line ends an event; lines that
/// start with a colon are comments; <c>id</c> and <c>retry</c> are ignored. Not thread-safe.
/// </summary>
public sealed class SseParser
{
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string? _name;
    private bool _hasData;
    private bool _lastWasCr;

    /// <summary>Feeds more text and returns the events that it completed (possibly none).</summary>
    /// <param name="chunk">The next piece of the stream, already decoded.</param>
    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<char> chunk)
    {
        List<SseEvent>? events = null;
        foreach (var c in chunk)
        {
            if (c == '\n' && _lastWasCr)
            {
                // The LF of a CR LF pair whose CR already ended the line.
                _lastWasCr = false;
                continue;
            }

            _lastWasCr = c == '\r';
            if (c is '\r' or '\n')
            {
                if (EndLine() is { } done)
                {
                    (events ??= []).Add(done);
                }
            }
            else
            {
                _line.Append(c);
            }
        }

        return events ?? (IReadOnlyList<SseEvent>)[];
    }

    private SseEvent? EndLine()
    {
        var line = _line.ToString();
        _line.Clear();
        if (line.Length == 0)
        {
            return Dispatch();
        }

        if (line[0] == ':')
        {
            return null;
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        switch (field)
        {
            case "event":
                _name = value;
                break;
            case "data":
                if (_hasData)
                {
                    _data.Append('\n');
                }

                _data.Append(value);
                _hasData = true;
                break;
        }

        return null;
    }

    private SseEvent? Dispatch()
    {
        if (!_hasData && _name is null)
        {
            return null;
        }

        var result = new SseEvent(string.IsNullOrEmpty(_name) ? "message" : _name, _data.ToString());
        _data.Clear();
        _hasData = false;
        _name = null;
        return result;
    }
}
