using AlertaIBT.App.Platform;

namespace AlertaIBT.App.Tests.Platform;

public sealed class StartupRegistrationTests
{
    private const string Exe = @"C:\Users\igreja\AppData\Local\AlertaIBT\current\AlertaIBT.exe";

    private sealed class FakeStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? FailWith { get; set; }

        public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public IReadOnlyDictionary<string, string> GetAll() => Values;

        public void Remove(string name) => Values.Remove(name);

        public void Set(string name, string command)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Values[name] = command;
        }
    }

    [Fact]
    public void The_value_is_named_AlertaIBT_and_holds_the_quoted_exe_with_the_tray_switch()
    {
        var store = new FakeStore();

        var result = new StartupRegistration(store, Exe, enabled: true).Apply();

        Assert.Equal(StartupRegistrationResult.Written, result);
        Assert.Equal("AlertaIBT", StartupRegistration.ValueName);
        Assert.Equal($"\"{Exe}\" --tray", store.Values["AlertaIBT"]);
    }

    [Fact]
    public void A_path_with_spaces_stays_one_quoted_argument()
    {
        Assert.Equal("\"C:\\Program Files\\Igreja App\\AlertaIBT.exe\" --tray", StartupRegistration.BuildCommand(@"C:\Program Files\Igreja App\AlertaIBT.exe"));
    }

    [Fact]
    public void A_correct_entry_is_left_alone_and_a_stale_one_is_repaired()
    {
        var store = new FakeStore();
        store.Values["AlertaIBT"] = StartupRegistration.BuildCommand(Exe);
        Assert.Equal(StartupRegistrationResult.AlreadyCorrect, new StartupRegistration(store, Exe, true).Apply());

        store.Values["AlertaIBT"] = "\"D:\\old\\AlertaIBT.exe\" --tray";
        Assert.Equal(StartupRegistrationResult.Written, new StartupRegistration(store, Exe, true).Apply());
        Assert.Equal(StartupRegistration.BuildCommand(Exe), store.Values["AlertaIBT"]);
    }

    [Fact]
    public void A_development_build_writes_nothing()
    {
        var store = new FakeStore();

        Assert.Equal(StartupRegistrationResult.Disabled, new StartupRegistration(store, Exe, enabled: false).Apply());
        Assert.Empty(store.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_known_exe_nothing_is_written(string? exe)
    {
        var store = new FakeStore();

        Assert.Equal(StartupRegistrationResult.NoExecutablePath, new StartupRegistration(store, exe, true).Apply());
        Assert.Empty(store.Values);
    }

    [Fact]
    public void A_refused_write_is_reported_not_thrown()
    {
        var store = new FakeStore { FailWith = new UnauthorizedAccessException("locked down") };
        Exception? reported = null;

        var result = new StartupRegistration(store, Exe, true).Apply(ex => reported = ex);

        Assert.Equal(StartupRegistrationResult.Failed, result);
        Assert.IsType<UnauthorizedAccessException>(reported);
    }

    [Fact]
    public void Other_exceptions_are_not_swallowed()
    {
        var store = new FakeStore { FailWith = new InvalidOperationException("bug") };

        Assert.Throws<InvalidOperationException>(() => new StartupRegistration(store, Exe, true).Apply());
    }
}
