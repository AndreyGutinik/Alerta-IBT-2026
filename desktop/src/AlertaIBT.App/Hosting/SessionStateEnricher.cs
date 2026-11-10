using AlertaIBT.Core.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace AlertaIBT.App.Hosting;

/// <summary>
/// Adds the session state (locked or not, display on or off, remote or local, the last resume) to every line of level Error or Fatal, right after the message: a
/// window-system or audio error means something different in a locked session than in an unlocked one, and the cause is rarely visible in the line alone. The text is
/// built from <see cref="SessionState"/> only (no setting, no secret).
/// </summary>
public sealed class SessionStateEnricher(SessionState? state = null) : ILogEventEnricher
{
    /// <summary>The property the output template prints (<c>{Sessao:l}</c>).</summary>
    public const string PropertyName = "Sessao";

    private readonly SessionState _state = state ?? SessionState.Default;

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        if (logEvent.Level < LogEventLevel.Error)
        {
            return;
        }

        string text;
        try
        {
            text = " [" + _state.Describe() + "]";
        }
        catch (Exception)
        {
            return; // a line is never lost because its context could not be built
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, text));
    }
}
