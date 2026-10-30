using AlertaIBT.Core.Settings;

namespace AlertaIBT.Core.Tests.Player;

public sealed class CrossfadeCurveTests
{
    private static readonly CrossfadeCurve[] All = [CrossfadeCurve.Linear, CrossfadeCurve.ConstantPower, CrossfadeCurve.Intermediate];

    [Fact]
    public void Default_is_linear_and_unknown_text_is_linear()
    {
        Assert.Equal(CrossfadeCurve.Linear, new PlayerSettings().CrossfadeCurve);
        Assert.Equal(CrossfadeCurve.Linear, CrossfadeCurves.Parse(null));
        Assert.Equal(CrossfadeCurve.Linear, CrossfadeCurves.Parse("lixo"));
        foreach (var curve in All)
        {
            Assert.Equal(curve, CrossfadeCurves.Parse(CrossfadeCurves.ToText(curve)));
        }
    }

    [Theory]
    [InlineData(CrossfadeCurve.Linear)]
    [InlineData(CrossfadeCurve.ConstantPower)]
    [InlineData(CrossfadeCurve.Intermediate)]
    public void Ends_are_exact_and_the_curve_is_monotonic_and_continuous(CrossfadeCurve curve)
    {
        Assert.Equal((1f, 0f), CrossfadeCurves.Gains(curve, 0));
        Assert.Equal((0f, 1f), CrossfadeCurves.Gains(curve, 1));
        Assert.Equal((1f, 0f), CrossfadeCurves.Gains(curve, -3));
        Assert.Equal((0f, 1f), CrossfadeCurves.Gains(curve, 7));
        var (previousOut, previousIn) = (1f, 0f);
        for (var i = 1; i <= 1000; i++)
        {
            var (o, n) = CrossfadeCurves.Gains(curve, i / 1000.0);
            Assert.True(o <= previousOut && n >= previousIn, $"{curve} not monotonic at {i}");
            Assert.True(previousOut - o < 0.01 && n - previousIn < 0.01, $"{curve} jumps at {i}");
            (previousOut, previousIn) = (o, n);
        }
    }

    [Fact]
    public void Values_and_midpoint_dips()
    {
        Assert.Equal(0.75f, CrossfadeCurves.Gains(CrossfadeCurve.Linear, 0.25).Outgoing, 5);
        Assert.Equal(Math.Cos(Math.PI / 8), CrossfadeCurves.Gains(CrossfadeCurve.ConstantPower, 0.25).Outgoing, 5);
        var inter = CrossfadeCurves.Gains(CrossfadeCurve.Intermediate, 0.25);
        Assert.InRange(inter.Outgoing, 0.83f, 0.85f);
        Assert.InRange(CrossfadeCurves.Gains(CrossfadeCurve.Intermediate, 0.5).Outgoing, 0.59f, 0.61f);

        static double Db(CrossfadeCurve c)
        {
            var (o, n) = CrossfadeCurves.Gains(c, 0.5);
            return 10 * Math.Log10((o * o) + (n * n));
        }

        Assert.Equal(-3.01, Db(CrossfadeCurve.Linear), 1);
        Assert.Equal(0, Db(CrossfadeCurve.ConstantPower), 2);
        Assert.Equal(-1.37, Db(CrossfadeCurve.Intermediate), 1);
    }

    [Fact]
    public void Settings_roundtrip_and_missing_value_is_linear()
    {
        var settings = SettingsJson.Load(null).Settings;
        var json = SettingsJson.Save(settings with { Player = settings.Player with { CrossfadeCurve = CrossfadeCurve.Intermediate } });
        Assert.Equal(CrossfadeCurve.Intermediate, SettingsJson.Load(json).Settings.Player.CrossfadeCurve);
        Assert.Equal(CrossfadeCurve.Linear, SettingsJson.Load("{\"player\":{\"crossfadeSeconds\":5}}").Settings.Player.CrossfadeCurve);
    }
}
