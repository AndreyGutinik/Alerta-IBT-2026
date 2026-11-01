using AlertaIBT.Core.Holyrics;
using AlertaIBT.Core.Player;

namespace AlertaIBT.App.Tests.ViewModels;

/// <summary>A scripted Holyrics countdown: whatever <see cref="Next"/> holds is what the watcher reads.</summary>
internal sealed class FakeCountdownSource : IHolyricsCountdownSource
{
    public bool IsConfigured { get; set; } = true;

    public CountdownReading Next { get; set; } = new(HolyricsOutcome.Ok);

    public Task<CountdownReading> ReadCountdownAsync(CancellationToken cancellationToken) => Task.FromResult(Next);
}

/// <summary>A player that only counts the pauses.</summary>
internal sealed class CountingPlayer : IPlayerControl
{
    public PlayerState State { get; set; } = PlayerState.Playing;

    public int Pauses { get; private set; }

    public PlayerResult Play() => PlayerResult.Ok;

    public PlayerResult Pause()
    {
        Pauses++;
        return PlayerResult.Ok;
    }

    public PlayerResult Stop() => PlayerResult.Ok;

    public PlayerResult Next() => PlayerResult.Ok;

    public PlayerResult Previous() => PlayerResult.Ok;

    public PlayerStatus GetStatus() =>
        new(true, State, false, null, TimeSpan.Zero, TimeSpan.Zero, "x", 0, 100, false, false, PlayerRepeat.Off, OutputStatus.Unknown, null);
}
