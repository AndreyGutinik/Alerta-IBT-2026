using AlertaIBT.App.ViewModels;

namespace AlertaIBT.App.Tests.ViewModels;

public sealed class FieldsTests
{
    // ---- IntegerField ----

    private sealed class IntRig
    {
        public IntRig(int min = 8, int max = 200, bool allowBlank = true)
        {
            Field = new IntegerField(min, max, allowBlank, value => Committed.Add(value), () => Cancels++);
        }

        public IntegerField Field { get; }

        public List<int?> Committed { get; } = [];

        public int Cancels { get; private set; }
    }

    [Fact]
    public void A_number_in_range_is_handed_on_as_it_is()
    {
        var rig = new IntRig();

        rig.Field.Text = "64";

        Assert.Equal([64], rig.Committed);
        Assert.Null(rig.Field.Hint);
        Assert.Equal(64, rig.Field.Accepted);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1,5")]
    [InlineData("--3")]
    [InlineData("\U000000BD")]
    public void Something_that_is_not_a_whole_number_is_ignored_with_a_hint_and_never_handed_on(string typed)
    {
        var rig = new IntRig();
        rig.Field.Set(64);

        rig.Field.Text = typed;

        Assert.Empty(rig.Committed);
        Assert.NotNull(rig.Field.Hint);
        Assert.True(rig.Field.HasHint);
        Assert.Equal(1, rig.Cancels); // a waiting save of an older value is dropped
        Assert.Equal(64, rig.Field.Accepted);
    }

    [Theory]
    [InlineData("2", 8, "Ajustado para 8")]
    [InlineData("-40", 8, "Ajustado para 8")]
    [InlineData("500", 200, "Ajustado para 200")]
    [InlineData("99999999999999999999", 200, "Ajustado para 200")]
    [InlineData("-99999999999999999999", 8, "Ajustado para 8")]
    public void A_number_out_of_range_is_held_in_range_and_only_the_held_value_is_handed_on(string typed, int expected, string hint)
    {
        var rig = new IntRig();

        rig.Field.Text = typed;

        Assert.Equal([expected], rig.Committed);
        Assert.StartsWith(hint, rig.Field.Hint, StringComparison.Ordinal);
        Assert.Equal(expected, rig.Field.Accepted);
    }

    [Fact]
    public void Blank_means_automatic_when_allowed_and_a_mistake_when_not()
    {
        var automatic = new IntRig(allowBlank: true);
        automatic.Field.Set(60);
        automatic.Field.Text = "  ";

        Assert.Equal([(int?)null], automatic.Committed);
        Assert.Null(automatic.Field.Hint);

        var required = new IntRig(allowBlank: false);
        required.Field.Set(24);
        required.Field.Text = "";

        Assert.Empty(required.Committed);
        Assert.NotNull(required.Field.Hint);
        Assert.Equal(1, required.Cancels);
    }

    [Fact]
    public void Set_puts_a_value_in_the_box_without_handing_it_on()
    {
        var rig = new IntRig();

        rig.Field.Set(72);

        Assert.Equal("72", rig.Field.Text);
        Assert.Empty(rig.Committed);
        Assert.Equal(72, rig.Field.Accepted);
    }

    [Fact]
    public void Losing_the_focus_shows_the_value_that_was_kept_never_the_garbage()
    {
        var rig = new IntRig();
        rig.Field.Set(64);
        rig.Field.Text = "abc";

        rig.Field.Normalize();

        Assert.Equal("64", rig.Field.Text);
        Assert.Null(rig.Field.Hint);

        rig.Field.Text = "5000";
        rig.Field.Normalize();
        Assert.Equal("200", rig.Field.Text);
        Assert.Equal(200, rig.Field.Accepted);
    }

    // ---- ColorField ----

    [Theory]
    [InlineData("#fff7e8", "#FFF7E8")]
    [InlineData("FFF7E8", "#FFF7E8")]
    [InlineData("  #00ff00 ", "#00FF00")]
    public void A_color_is_handed_on_as_upper_case_hash_rrggbb(string typed, string expected)
    {
        var committed = new List<string>();
        var field = new ColorField(committed.Add, () => { });

        field.Text = typed;

        Assert.Equal([expected], committed);
        Assert.Null(field.Hint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#fff")]
    [InlineData("red")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345678")]
    public void Anything_else_is_ignored_with_a_hint(string typed)
    {
        var committed = new List<string>();
        var cancels = 0;
        var field = new ColorField(committed.Add, () => cancels++);
        field.Set("#000000");

        field.Text = typed;

        Assert.Empty(committed);
        Assert.NotNull(field.Hint);
        Assert.Equal(1, cancels);
        Assert.Equal("#000000", field.Accepted);

        field.Normalize();
        Assert.Equal("#000000", field.Text);
        Assert.Null(field.Hint);
    }

    // ---- SecretFieldViewModel ----

    private sealed class SecretRig
    {
        public SecretRig(bool stored)
        {
            Stored = stored;
            Field = new SecretFieldViewModel(() => Stored, Validate, secret => Committed.Add(secret), () => Cancels++);
        }

        public bool Stored { get; set; }

        public SecretFieldViewModel Field { get; }

        public List<string> Committed { get; } = [];

        public int Cancels { get; private set; }

        private static string? Validate(string secret) => secret.Length >= 8 ? null : "curto";
    }

    [Fact]
    public void A_stored_secret_opens_locked_with_dots_and_no_part_of_it_anywhere()
    {
        var rig = new SecretRig(stored: true);

        Assert.True(rig.Field.IsLocked);
        Assert.False(rig.Field.IsEditing);
        Assert.Equal("Trocar", rig.Field.LockButtonText);
        Assert.True(rig.Field.CanLock);
        Assert.Equal(string.Empty, rig.Field.Entry);
        Assert.All(SecretFieldViewModel.Mask, c => Assert.Equal('\U00002022', c));
    }

    [Fact]
    public void Without_a_stored_secret_the_field_is_open_and_has_nothing_to_lock()
    {
        var rig = new SecretRig(stored: false);

        Assert.False(rig.Field.IsLocked);
        Assert.True(rig.Field.IsEditing);
        Assert.False(rig.Field.CanLock);
    }

    [Fact]
    public void Trocar_opens_an_empty_entry_and_a_good_secret_is_handed_on_once()
    {
        var rig = new SecretRig(stored: true);

        rig.Field.Unlock();
        rig.Field.Entry = "  12345678-secret ";

        Assert.False(rig.Field.IsLocked);
        Assert.Equal(["12345678-secret"], rig.Committed);
        Assert.Equal("Bloquear", rig.Field.LockButtonText);
    }

    [Fact]
    public void Once_committed_the_secret_is_forgotten_and_the_field_is_locked_again()
    {
        var rig = new SecretRig(stored: true);
        rig.Field.Unlock();
        rig.Field.Entry = "12345678-secret";

        rig.Field.Committed();

        Assert.Equal(string.Empty, rig.Field.Entry);
        Assert.True(rig.Field.IsLocked);
        Assert.Equal(["12345678-secret"], rig.Committed); // emptying the entry handed nothing more on
        Assert.Equal(0, rig.Cancels);
    }

    [Fact]
    public void The_first_secret_ever_stored_locks_the_field_after_the_save()
    {
        var rig = new SecretRig(stored: false);
        rig.Field.Entry = "12345678-secret";
        rig.Stored = true;

        rig.Field.Committed();

        Assert.True(rig.Field.HasStored);
        Assert.True(rig.Field.IsLocked);
        Assert.Equal(string.Empty, rig.Field.Entry);
    }

    [Fact]
    public void A_secret_that_fails_the_check_is_not_handed_on_and_the_hint_says_so()
    {
        var rig = new SecretRig(stored: true);
        rig.Field.Unlock();

        rig.Field.Entry = "short";

        Assert.Empty(rig.Committed);
        Assert.Equal("curto", rig.Field.Hint);
        Assert.Equal(1, rig.Cancels);
    }

    [Fact]
    public void Emptying_the_entry_cancels_the_waiting_save_and_does_not_erase_the_stored_secret()
    {
        var rig = new SecretRig(stored: true);
        rig.Field.Unlock();
        rig.Field.Entry = "12345678-secret";

        rig.Field.Entry = string.Empty;

        Assert.Equal(1, rig.Cancels);
        Assert.Equal(["12345678-secret"], rig.Committed); // the earlier hand-over was dropped by the cancel, nothing was erased
        Assert.True(rig.Field.HasStored);
    }

    [Fact]
    public void Locking_again_throws_away_what_was_typed()
    {
        var rig = new SecretRig(stored: true);
        rig.Field.Unlock();
        rig.Field.Entry = "12345678-secret";

        rig.Field.Lock();

        Assert.True(rig.Field.IsLocked);
        Assert.Equal(string.Empty, rig.Field.Entry);
        Assert.Equal(1, rig.Cancels);
    }

    [Fact]
    public void Typing_in_a_locked_field_does_nothing()
    {
        var rig = new SecretRig(stored: true);

        rig.Field.Entry = "12345678-secret";

        Assert.Empty(rig.Committed);
    }

    [Fact]
    public void The_lock_button_toggles_and_a_field_with_nothing_stored_cannot_be_locked()
    {
        var stored = new SecretRig(stored: true);
        stored.Field.ToggleLock();
        Assert.False(stored.Field.IsLocked);
        stored.Field.ToggleLock();
        Assert.True(stored.Field.IsLocked);

        var empty = new SecretRig(stored: false);
        empty.Field.ToggleLock();
        Assert.False(empty.Field.IsLocked);
    }
}
