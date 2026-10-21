using AlertaIBT.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertaIBT.App.ViewModels;

/// <summary>Where the "Salvo" stamp of the tabs stands.</summary>
public enum SaveState
{
    /// <summary>Everything the user changed is on disk.</summary>
    Saved,

    /// <summary>A change waits for its 600 ms of rest.</summary>
    Saving,

    /// <summary>The last save did not reach the disk.</summary>
    Failed,
}

/// <summary>
/// The autosave of every settings control (spec 7: no Save button anywhere). A control that changed hands over what it wants to
/// change as a function of the settings; 600 ms after the last such hand-over all the waiting changes go through
/// <see cref="SettingsService.Update"/> in one call, the file is written at once, and the stamp of the tabs says "Salvo".
/// </summary>
/// <remarks>
/// A change names its <c>key</c> (one per control): a newer change under the same key replaces the older one, and
/// <see cref="Cancel"/> drops a change that turned invalid before its time came, so garbage is never saved. A change is a
/// function of the settings at the moment of saving, so a value written meanwhile by something else (the Telegram offset, the
/// remembered LED) is never overwritten with a stale copy.
/// </remarks>
public sealed partial class Autosaver : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly Action<Action> _post;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = [];
    private bool _disposed;

    [ObservableProperty]
    private SaveState _state = SaveState.Saved;

    [ObservableProperty]
    private string _label = SavedLabel;

    /// <param name="settings">Where the changes go.</param>
    /// <param name="post">Runs an action on the UI thread; runs it at once when null (tests).</param>
    /// <param name="clock">Drives the 600 ms rest and dates the stamp.</param>
    /// <param name="logger">Receives failures (never a value).</param>
    public Autosaver(SettingsService settings, Action<Action>? post = null, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _post = post ?? (action => action());
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _timer = _clock.CreateTimer(_ => _post(FlushNow), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The stamp when everything is saved and nothing was saved yet in this run: <c>\U00002713 Salvo</c>.</summary>
    public static string SavedLabel => "\U00002713 Salvo";

    /// <summary>How long the controls must rest before their changes are saved.</summary>
    public static TimeSpan Delay => SettingsService.SaveDelay;

    /// <summary>Whether a change is waiting for its time.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>Raised after a batch went through the settings (and the file was written, or failed to be). On the UI thread.</summary>
    public event Action? Applied;

    /// <summary>
    /// Hands over a change; it is saved 600 ms after the last change of any control.
    /// </summary>
    /// <param name="key">The control; a newer change with the same key replaces the waiting one.</param>
    /// <param name="edit">What to change in the settings (pure: it runs inside the settings lock).</param>
    /// <param name="afterApply">Runs on the UI thread right after the change went in (before <see cref="Applied"/>).</param>
    public void Queue(string key, Func<AppSettings, AppSettings> edit, Action? afterApply = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(edit);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending[key] = new Pending(edit, afterApply);
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }

        SetSaving();
    }

    /// <summary>Drops the waiting change of <paramref name="key"/>, if any (the control now holds something invalid).</summary>
    public void Cancel(string key)
    {
        bool none;
        lock (_gate)
        {
            if (!_pending.Remove(key))
            {
                return;
            }

            none = _pending.Count == 0;
            if (none)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        if (none && State == SaveState.Saving)
        {
            State = SaveState.Saved;
            Label = SavedLabel;
        }
    }

    /// <summary>
    /// Saves the waiting changes now (the 600 ms rest is cut short): before the real bar shows its test, and on the way out.
    /// Never throws.
    /// </summary>
    public void FlushNow()
    {
        List<Pending> batch;
        lock (_gate)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending.Values];
            _pending.Clear();
        }

        var saved = true;
        try
        {
            _settings.Update(settings => batch.Aggregate(settings, (current, change) => change.Edit(current)));
            _settings.Flush();
            saved = !_settings.HasUnsavedChanges;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar as configuracoes");
            saved = false;
        }

        foreach (var change in batch)
        {
            if (change.After is null)
            {
                continue;
            }

            try
            {
                change.After();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao concluir uma configuracao salva");
            }
        }

        if (HasPending)
        {
            SetSaving(); // something changed while this batch was being saved
        }
        else if (saved)
        {
            State = SaveState.Saved;
            Label = $"{SavedLabel} \U000000E0s {_clock.GetLocalNow():HH:mm:ss}";
        }
        else
        {
            State = SaveState.Failed;
            Label = "N\U000000E3o foi poss\U000000EDvel salvar";
        }

        Applied?.Invoke();
    }

    /// <summary>Saves what is waiting and stops the timer.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        FlushNow();
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    private void SetSaving()
    {
        State = SaveState.Saving;
        Label = "Salvando\U00002026";
    }

    private sealed record Pending(Func<AppSettings, AppSettings> Edit, Action? After);
}
