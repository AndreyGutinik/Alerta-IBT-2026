using System.Text.Json.Nodes;
using AlertaIBT.Core.Firebase;

namespace AlertaIBT.Core.Tests.Firebase;

// The owner (SiteContract.ProtectedAdmins) is always an administrator and never blocked, whatever /users says.
public sealed class SiteEngineOwnerTests
{
    private const string Owner = "gutnikandrey01@protonmail.com";

    private static JsonObject User(string email, string role, bool blocked) => new()
    {
        ["name"] = "Dono",
        ["email"] = email,
        ["role"] = role,
        ["blocked"] = blocked,
        ["createdAt"] = 1,
    };

    [Fact]
    public void The_owner_address_is_written_once_and_matched_without_case_or_spaces()
    {
        Assert.Equal([Owner], SiteContract.ProtectedAdmins);
        Assert.True(SiteContract.IsProtectedAdmin(Owner));
        Assert.True(SiteContract.IsProtectedAdmin("  GutnikAndrey01@ProtonMail.com \t"));
        Assert.False(SiteContract.IsProtectedAdmin(null));
        Assert.False(SiteContract.IsProtectedAdmin(""));
        Assert.False(SiteContract.IsProtectedAdmin("gutnikandrey01@protonmail.com.br"));
        Assert.False(SiteContract.IsProtectedAdmin("xgutnikandrey01@protonmail.com"));
        Assert.False(SiteContract.IsProtectedAdmin("gutnikandrey01@gmail.com"));
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData("GUTNIKANDREY01@PROTONMAIL.COM")]
    [InlineData("  gutnikandrey01@protonmail.com  ")]
    public async Task The_owner_is_processed_as_an_admin_even_if_blocked_and_marked_member(string email)
    {
        await using var rig = new SiteRig(ledOn: false);
        rig.Fake.Set("users/u-owner", User(email, "member", blocked: true));
        await rig.StartOnlineAsync();

        // A free notice is for admins only, and eleven in the hour is more than a member may send.
        rig.Send("free", "pais do pedro henrique favor comparecer a recepcao", uid: "u-owner");
        for (var i = 0; i < 11; i++)
        {
            rig.Send("r" + i, "corolla prata abc1d2" + (i % 10), uid: "u-owner");
        }

        await rig.WaitState("free", "queued");
        await rig.WaitFor(() => Enumerable.Range(0, 11).All(i => rig.StateOf("r" + i) is not null), "all answered");
        Assert.DoesNotContain(Enumerable.Range(0, 11), i => rig.ReasonOf("r" + i) is "acesso removido" or "limite de 10 por hora");
    }

    [Fact]
    public async Task Other_users_are_unaffected_a_blocked_one_stays_blocked_and_a_member_stays_limited()
    {
        await using var rig = new SiteRig(ledOn: false);
        rig.Fake.Set("users/u-look-alike", User("gutnikandrey01@protonmail.com.br", "member", blocked: true));
        rig.Fake.Set("users/u-no-email", new JsonObject { ["name"] = "X", ["role"] = "member", ["blocked"] = true, ["createdAt"] = 1 });
        rig.Fake.Set("users/u-number-email", new JsonObject { ["name"] = "X", ["email"] = 7, ["role"] = "member", ["blocked"] = true, ["createdAt"] = 1 });
        rig.Fake.Set("users/u-member2", User("outra@x.com", "member", blocked: false));
        await rig.StartOnlineAsync();

        rig.Send("b1", SiteRig.Good, uid: "u-look-alike");
        rig.Send("b2", SiteRig.Good, uid: "u-no-email");
        rig.Send("b3", SiteRig.Good, uid: "u-number-email");
        for (var i = 0; i < 10; i++)
        {
            rig.Send("m" + i, "corolla prata abc1d2" + i, uid: "u-member2");
        }

        rig.Send("m10", "corolla prata abd1d20", uid: "u-member2");
        await rig.WaitState("b1", "rejected");
        await rig.WaitState("b2", "rejected");
        await rig.WaitState("b3", "rejected");
        await rig.WaitState("m10", "rejected");

        Assert.Equal("acesso removido", rig.ReasonOf("b1"));
        Assert.Equal("acesso removido", rig.ReasonOf("b2"));
        Assert.Equal("acesso removido", rig.ReasonOf("b3"));
        Assert.Equal("limite de 10 por hora", rig.ReasonOf("m10"));
    }
}
