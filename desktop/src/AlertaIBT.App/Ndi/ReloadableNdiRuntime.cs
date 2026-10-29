using AlertaIBT.Core.Ndi;

namespace AlertaIBT.App.Ndi;

/// <summary>
/// The NDI runtime that can look for the NDI Tools again: when the first load found nothing (the owner installs NDI Tools while the app runs),
/// <see cref="Recheck"/> loads again and, if it works now, the engine uses it from then on (the engine reads <see cref="IsAvailable"/> live).
/// </summary>
public sealed class ReloadableNdiRuntime : INdiRuntime
{
    private readonly Func<INdiRuntime> _load;
    private readonly object _gate = new();
    private INdiRuntime _inner;

    /// <param name="load">Loads a runtime (<c>NdiLibrary.Load</c> in the app).</param>
    public ReloadableNdiRuntime(Func<INdiRuntime> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
        _inner = load();
    }

    /// <inheritdoc />
    public bool IsAvailable => Inner.IsAvailable;

    /// <inheritdoc />
    public string Version => Inner.Version;

    /// <inheritdoc />
    public string? UnavailableReason => Inner.UnavailableReason;

    /// <inheritdoc />
    public string? LoadedFrom => Inner.LoadedFrom;

    private INdiRuntime Inner
    {
        get
        {
            lock (_gate)
            {
                return _inner;
            }
        }
    }

    /// <summary>Looks for the runtime again when it is not available; true when it works (it was already working, or it was found now).</summary>
    public bool Recheck()
    {
        lock (_gate)
        {
            if (_inner.IsAvailable)
            {
                return true;
            }

            var old = _inner;
            _inner = _load();
            old.Dispose();
            return _inner.IsAvailable;
        }
    }

    /// <inheritdoc />
    public INdiFinder CreateFinder(IReadOnlyList<string> extraIps) => Inner.CreateFinder(extraIps);

    /// <inheritdoc />
    public INdiNativeReceiver CreateReceiver(NdiSourceRef source, string receiverName) => Inner.CreateReceiver(source, receiverName);

    /// <inheritdoc />
    public void Dispose() => Inner.Dispose();
}
