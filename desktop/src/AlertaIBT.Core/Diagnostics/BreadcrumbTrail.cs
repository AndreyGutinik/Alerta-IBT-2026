using System.Globalization;
using System.Text;

namespace AlertaIBT.Core.Diagnostics;

/// <summary>
/// The last few things the app did, kept in a tiny ring buffer so that a log line written when something goes wrong (the screen stops answering,
/// a crash) can say what happened just before. An entry is a short phrase (a setting's KEY NAME, a player command, a scanner step, an API route
/// without its query): never a value that may be a secret, never a path of user data beyond a file name. Adding is a few hundred nanoseconds and
/// safe from any thread; nothing is written anywhere until somebody asks for <see cref="Format"/>.
/// </summary>
public sealed class BreadcrumbTrail
{
    /// <summary>How many entries the trail keeps.</summary>
    public const int DefaultCapacity = 24;

    private readonly object _gate = new();
    private readonly Entry[] _ring;
    private readonly TimeProvider _clock;
    private int _next;
    private int _count;

    /// <param name="capacity">How many entries to keep.</param>
    /// <param name="clock">Stamps the entries.</param>
    public BreadcrumbTrail(int capacity = DefaultCapacity, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _ring = new Entry[capacity];
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The trail of the running app.</summary>
    public static BreadcrumbTrail Default { get; } = new();

    /// <summary>Records one entry (it pushes out the oldest when the trail is full).</summary>
    public void Add(string text)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            _ring[_next] = new Entry(now, text);
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>The entries, oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, string Text)> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<(DateTimeOffset, string)>(_count);
            var start = (_next - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                var entry = _ring[(start + i) % _ring.Length];
                list.Add((entry.At, entry.Text));
            }

            return list;
        }
    }

    /// <summary>The newest <paramref name="max"/> entries as one text, oldest first, each with its time (<c>HH:mm:ss.fff</c>, local) and how long before <paramref name="now"/> it happened.</summary>
    public string Format(DateTimeOffset now, int max = DefaultCapacity)
    {
        var entries = Snapshot();
        if (entries.Count == 0)
        {
            return "(nenhuma operacao registrada)";
        }

        var text = new StringBuilder();
        foreach (var (at, entry) in entries.Skip(Math.Max(0, entries.Count - max)))
        {
            if (text.Length > 0)
            {
                text.Append(" | ");
            }

            text.Append(at.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" (-")
                .Append(Math.Max(0, (now - at).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" s) ")
                .Append(entry);
        }

        return text.ToString();
    }

    private readonly record struct Entry(DateTimeOffset At, string Text);
}

/// <summary>Shorthand to record on the app's own <see cref="BreadcrumbTrail"/>.</summary>
public static class Breadcrumbs
{
    /// <summary>Records <paramref name="text"/> (a short phrase without secrets).</summary>
    public static void Add(string text) => BreadcrumbTrail.Default.Add(text);
}
