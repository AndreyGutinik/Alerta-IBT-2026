using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.Services;

/// <summary>
/// What the windows of the PC (the quick panel, later the main window) do with the alert service: type an alert as the
/// operator, preview it, pause and resume, and watch the queue. A seam so the view-models are tested without Gemini, the
/// bar or Telegram.
/// </summary>
public interface IAlertGateway
{
    /// <summary>The queue (what is on the bar, what waits). Its <see cref="AlertQueue.Changed"/> may fire on any thread.</summary>
    AlertQueue Queue { get; }

    /// <summary>What the typed text would turn into, without sending it.</summary>
    Task<PreviewResult> PreviewAsync(string raw, CancellationToken ct);

    /// <summary>Sends the typed text to the bar as the operator at the PC.</summary>
    Task<SubmitOutcome> SubmitAsync(string raw, bool urgent, CancellationToken ct);

    /// <summary>
    /// Starts or ends the sermon pause, exactly once per call. The tray menu does the same through the shell, which then
    /// only notifies; nothing else may toggle in response.
    /// </summary>
    void TogglePause();

    /// <summary>"Próxima" and "Encerrar atual": ends the alert on the bar now (it counts as shown) and the next one starts.</summary>
    void Skip();

    /// <summary>Moves a waiting alert up (negative) or down (positive) in the queue; false when it is no longer waiting.</summary>
    bool Move(string id, int delta);

    /// <summary>Removes a waiting alert at the PC; false when it is no longer waiting.</summary>
    bool Remove(string id);

    /// <summary>Removes every waiting alert (the one on the bar keeps going). No question asked.</summary>
    void ClearQueue();

    /// <summary>"Exibir de novo": queues a shown alert again, from the PC.</summary>
    SubmitOutcome Replay(QueueItem shown);
}

/// <summary>The real <see cref="IAlertGateway"/>: the PC is the operator, so free text is allowed and a repeated plate only warns.</summary>
public sealed class AlertGateway(AlertService alerts, AlertRuntime runtime) : IAlertGateway
{
    /// <inheritdoc />
    public AlertQueue Queue => alerts.Queue;

    /// <inheritdoc />
    public Task<PreviewResult> PreviewAsync(string raw, CancellationToken ct) =>
        alerts.PreviewAsync(raw, SenderRole.Operator, ct);

    /// <inheritdoc />
    public Task<SubmitOutcome> SubmitAsync(string raw, bool urgent, CancellationToken ct) =>
        alerts.SubmitAsync(raw, AlertSource.Pc, SenderRole.Operator, urgent, senderId: null, ct);

    /// <inheritdoc />
    public void TogglePause() => runtime.TogglePause();

    /// <inheritdoc />
    public void Skip() => alerts.Skip();

    /// <inheritdoc />
    public bool Move(string id, int delta) => alerts.Queue.Move(id, delta);

    /// <inheritdoc />
    public bool Remove(string id) => alerts.Queue.Remove(id);

    /// <inheritdoc />
    public void ClearQueue() => alerts.Clear();

    /// <inheritdoc />
    public SubmitOutcome Replay(QueueItem shown) => alerts.Replay(shown);
}
