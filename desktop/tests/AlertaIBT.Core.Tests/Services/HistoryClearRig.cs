using AlertaIBT.Core.Alerts;
using AlertaIBT.Core.Firebase;
using AlertaIBT.Core.Queue;
using AlertaIBT.Core.Services;
using AlertaIBT.Core.Settings;
using AlertaIBT.Core.Telegram;
using Microsoft.Extensions.Time.Testing;

namespace AlertaIBT.Core.Tests.Services;

internal sealed class FakeSiteHistory : ISiteHistory
{
    public SiteState State { get; set; } = SiteState.Online;

    public int Calls { get; private set; }

    public SiteClearResult Result { get; set; } = new(4, 0);

    public Exception? Throw { get; set; }

    public Task<SiteClearResult> ClearHistoryAsync(CancellationToken ct)
    {
        Calls++;
        return Throw is { } ex ? Task.FromException<SiteClearResult>(ex) : Task.FromResult(Result);
    }
}

internal sealed class FakeTelegramHistory : ITelegramHistory
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public TelegramClearResult Result { get; set; } = new(3, 0);

    public Exception? Throw { get; set; }

    /// <summary>When set, a call waits for it before it answers.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<TelegramClearResult> ClearAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return Throw is { } ex ? throw ex : Result;
    }
}

/// <summary>A queue, a fake site, a fake Telegram and settings in a box, on one fake clock.</summary>
internal sealed class HistoryClearRig
{
    public HistoryClearRig(bool siteConfigured = true, AppSettings? settings = null)
    {
        Settings = settings ?? new AppSettings();
        if (siteConfigured)
        {
            Settings = Settings with
            {
                Site = new SiteSettings
                {
                    Enabled = true,
                    DatabaseUrl = "https://x-default-rtdb.firebaseio.com",
                    ApiKey = "key",
                    DeviceEmail = "pc@x.com",
                    PasswordProtected = "protected",
                },
            };
        }

        Queue = new AlertQueue(Clock);
        Cleaner = new HistoryCleaner(Queue, () => CurrentSite, Telegram, () => Settings, Update, Clock, Log);
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    public AppSettings Settings { get; private set; }

    public AlertQueue Queue { get; }

    public FakeSiteHistory Site { get; } = new();

    public ISiteHistory? CurrentSite { get; set; }

    public FakeTelegramHistory Telegram { get; } = new();

    public CapturingLogger Log { get; } = new();

    public HistoryCleaner Cleaner { get; }

    public int SettingsWrites { get; private set; }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Settings = SettingsJson.Normalize(change(Settings));
        SettingsWrites++;
    }

    public void SiteIsOnline()
    {
        Site.State = SiteState.Online;
        CurrentSite = Site;
    }

    public void SiteIsOffline()
    {
        Site.State = SiteState.Offline;
        CurrentSite = Site;
    }

    public QueueItem Item(string id, string? plate = null) =>
        new(id, "TEXT " + id, plate, AlertSource.Telegram, false, Clock.GetUtcNow(), "42", "original " + id);

    public void FillHistory(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Queue.AddRejected(Item("h" + i), HistoryReasons.TooLong);
        }
    }
}
