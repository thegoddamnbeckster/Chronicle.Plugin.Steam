using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

[Collection("steam")]
public class SettingsTests
{
    private static Settings S(string account, string key = Fx.Key) =>
        Settings.From(new Dictionary<string, string> { ["api_key"] = key, ["steam_id"] = account });

    [Theory]
    [InlineData("76561198000000000", "76561198000000000", null)]
    [InlineData("https://steamcommunity.com/profiles/76561198000000000/", "76561198000000000", null)]
    [InlineData("steamcommunity.com/profiles/76561198000000000", "76561198000000000", null)]
    [InlineData("https://steamcommunity.com/id/some_name/", null, "some_name")]
    [InlineData("some_name", null, "some_name")]
    [InlineData("  some-name  ", null, "some-name")]
    public void The_account_box_takes_an_id_an_address_or_a_name(string typed, string? id, string? vanity)
    {
        var (i, v) = S(typed).ParseAccount();
        Assert.Equal(id, i);
        Assert.Equal(vanity, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("two words")]
    [InlineData("https://example.com/76561198000000000")]
    [InlineData("<script>")]
    public void Anything_else_is_not_an_account(string typed)
    {
        var (i, v) = S(typed).ParseAccount();
        Assert.Null(i);
        Assert.Null(v);
    }

    [Fact]
    public void A_key_with_whitespace_or_control_characters_is_refused_not_sent()
    {
        Assert.False(S("some_name", "0123 4567").HasCredentials);
        Assert.False(S("some_name", "key\nwith newline").HasCredentials);
        Assert.True(S("some_name").HasCredentials);
    }

    [Fact]
    public void Language_and_country_fall_back_when_they_are_not_plain_words()
    {
        var s = Settings.From(new Dictionary<string, string> { ["language"] = "en glish&x=1", ["country"] = "usa" });
        Assert.Equal("english", s.Language);
        Assert.Equal("us", s.Country);
        var ok = Settings.From(new Dictionary<string, string> { ["language"] = "French", ["country"] = "CA" });
        Assert.Equal("french", ok.Language);
        Assert.Equal("ca", ok.Country);
    }

    [Fact]
    public void Redact_removes_the_key_wherever_it_appears()
    {
        SteamHub.Configure(new Dictionary<string, string> { ["api_key"] = Fx.Key });
        try
        {
            var t = SteamHub.Redact($"GET https://api.steampowered.com/x?key={Fx.Key}&steamid=1 failed with {Fx.Key}");
            Assert.DoesNotContain(Fx.Key, t);
            Assert.Contains("key=***", t);
        }
        finally { SteamHub.ResetForTests(); }
    }
}
