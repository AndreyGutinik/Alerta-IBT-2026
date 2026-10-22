using AlertaIBT.Core.Audio;

namespace AlertaIBT.Core.Tests.Audio;

public sealed class AudioNamesTests
{
    [Theory]
    [InlineData("Spotify", "Spotify", true)]
    [InlineData("spotify", "Spotify", true)]
    [InlineData("SPOTIFY.EXE", "spotify", true)]
    [InlineData("Spotify.exe", "Spotify.exe", true)]
    [InlineData("Spotify", " spotify.exe ", true)]
    [InlineData("Spotify", "SpotifyHelper", false)]
    [InlineData("Spotify", "", false)]
    [InlineData("Spotify", null, false)]
    public void A_process_matches_by_name_ignoring_case_and_exe(string wanted, string? process, bool expected) =>
        Assert.Equal(expected, AudioNames.MatchesProcess([wanted], process));

    [Fact]
    public void Any_name_of_the_list_matches()
    {
        string[] names = ["Holyrics", "javaw"];

        Assert.True(AudioNames.MatchesProcess(names, "JAVAW"));
        Assert.True(AudioNames.MatchesProcess(names, "holyrics.exe"));
        Assert.False(AudioNames.MatchesProcess(names, "java"));
        Assert.False(AudioNames.MatchesProcess([], "holyrics"));
    }

    [Theory]
    [InlineData("Alto-falantes (WING USB)", "WING USB")]
    [InlineData("Alto-falantes (2- WING USB)", "WING USB")]
    [InlineData("Line (Behringer WING USB)", "Behringer WING USB")]
    [InlineData("SomenteNome", "SomenteNome")]
    [InlineData("PRS27-MPP01 (NVIDIA High Definition Audio)", "NVIDIA High Definition Audio")]
    public void The_short_name_is_what_is_inside_the_last_parentheses_without_the_duplicate_number(string name, string expected) =>
        Assert.Equal(expected, AudioNames.Short(name));

    [Fact]
    public void The_device_is_found_by_id_first_then_by_name_and_never_by_a_similar_one()
    {
        AudioDevice[] active =
        [
            new("{a}", "Alto-falantes (Realtek)"),
            new("{b}", "Alto-falantes (2- WING USB)"),
        ];

        Assert.Equal("{a}", AudioNames.Resolve(active, "{A}", "whatever")!.Id); // id wins, case does not matter
        Assert.Equal("{b}", AudioNames.Resolve(active, "{gone}", "alto-falantes  (WING USB)")!.Id); // same name, "2- " ignored
        Assert.Null(AudioNames.Resolve(active, "{gone}", "Alto-falantes (WING 2)"));
        Assert.Null(AudioNames.Resolve(active, null, null));
        Assert.Null(AudioNames.Resolve([], "{a}", "x"));
    }

    [Fact]
    public void The_first_output_containing_the_text_is_found_whatever_the_case()
    {
        AudioDevice[] active = [new("{a}", "Realtek"), new("{b}", "Line (behringer Wing usb)"), new("{c}", "WING 2")];

        Assert.Equal("{b}", AudioNames.FindContaining(active, "WING")!.Id);
        Assert.Null(AudioNames.FindContaining(active, "Yamaha"));
        Assert.Null(AudioNames.FindContaining(active, " "));
    }

    [Fact]
    public void All_three_roles_must_match_for_the_default_to_count_as_set()
    {
        Assert.True(new DefaultDevices("{A}", "{a}", "{A}").AllEqual("{a}"));
        Assert.False(new DefaultDevices("{a}", "{a}", "{b}").AllEqual("{a}"));
        Assert.False(new DefaultDevices(null, null, null).AllEqual("{a}"));
    }
}
