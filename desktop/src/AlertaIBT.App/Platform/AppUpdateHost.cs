using AlertaIBT.App.ViewModels;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;

namespace AlertaIBT.App.Platform;

/// <summary>
/// The running app as the update service sees it. Idle means: nothing waiting in the queue, nothing on the bar, the sermon pause
/// off (the pause is not saved, so a restart would end it and let alerts onto the bar during the sermon) and no send in flight in
/// the quick panel or the main window. It says "maybe idle" whenever one of those changes.
/// </summary>
public sealed class AppUpdateHost : IUpdateHost
{
    private readonly AlertQueue _queue;
    private readonly StatusHub _hub;
    private readonly Func<IReadOnlyList<QuickPanelViewModel>> _panels;
    private readonly Action _save;
    private readonly Action _exit;
    private int _attached;

    /// <param name="queue">The alert queue.</param>
    /// <param name="hub">The status of the parts (paused, showing).</param>
    /// <param name="panels">The message fields that can have a send in flight (the quick panel and the one of the main window).</param>
    /// <param name="save">Writes the queue and the settings now.</param>
    /// <param name="exit">Leaves through the app's normal shutdown.</param>
    public AppUpdateHost(
        AlertQueue queue,
        StatusHub hub,
        Func<IReadOnlyList<QuickPanelViewModel>> panels,
        Action save,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(panels);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(exit);

        _queue = queue;
        _hub = hub;
        _panels = panels;
        _save = save;
        _exit = exit;
    }

    /// <inheritdoc />
    public bool IsIdle =>
        _queue.Current is null
        && _queue.Pending.Count == 0
        && !_hub.Showing
        && !_hub.Paused
        && !_panels().Any(panel => panel.IsSending);

    /// <inheritdoc />
    public void Attach(Action maybeIdle)
    {
        ArgumentNullException.ThrowIfNull(maybeIdle);

        if (Interlocked.Exchange(ref _attached, 1) != 0)
        {
            return;
        }

        _queue.Changed += (_, _) => maybeIdle();
        _hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatusHub.Paused) or nameof(StatusHub.Showing))
            {
                maybeIdle();
            }
        };

        foreach (var panel in _panels())
        {
            panel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuickPanelViewModel.IsSending))
                {
                    maybeIdle();
                }
            };
        }
    }

    /// <inheritdoc />
    public void PrepareForRestart() => _save();

    /// <inheritdoc />
    public void Exit() => _exit();
}
