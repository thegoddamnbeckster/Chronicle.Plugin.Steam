using System.Text.Json;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

public class MapperTests
{
    private static JsonElement Game(int appid) =>
        Fx.Json("owned.json").GetProperty("response").GetProperty("games").EnumerateArray().First(g => g.GetProperty("appid").GetInt32() == appid);

    [Fact]
    public void An_unplayed_game_is_owned_with_zero_minutes_and_no_date()
    {
        var e = SteamMapper.Owned(Game(10));
        Assert.Equal("steam:10", e.ExternalId);
        Assert.Equal("Counter-Strike", e.Title);
        Assert.Equal(0, e.PlaytimeMinutes);
        Assert.Null(e.LastPlayedAt);              // Steam's 0 is "never", not 1970
        Assert.False(e.IsWishlist);
        Assert.Equal("PC", e.Platform);
        Assert.Equal("steam", e.StoreSource);
        Assert.Equal("game", e.MediaType);
    }

    [Fact]
    public void A_played_game_keeps_its_minutes_and_last_played()
    {
        var e = SteamMapper.Owned(Game(553850));
        Assert.True(e.PlaytimeMinutes > 0);
        Assert.NotNull(e.LastPlayedAt);
        Assert.True(e.LastPlayedAt > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void The_whole_record_travels_with_the_entry_including_what_nothing_reads_yet()
    {
        var e = SteamMapper.Owned(Game(553850));
        using var raw = JsonDocument.Parse(e.RawJson);
        foreach (var field in new[] { "playtime_windows_forever", "playtime_deck_forever", "playtime_disconnected", "has_workshop", "has_market", "has_dlc", "capsule_filename", "img_icon_url" })
            Assert.True(raw.RootElement.TryGetProperty(field, out _), field);
    }

    [Fact]
    public void The_library_and_the_store_half_write_one_id_for_one_game_in_one_form()
    {
        // They used to disagree ("steam:1245620" from the import, "1245620" from the metadata half), which made one game two items.
        var d = Fx.Json("appdetails-1245620.json").GetProperty("1245620").GetProperty("data");
        var fromStore = SteamMapper.Details("1245620", d, null).ExternalId;
        var fromLibrary = SteamMapper.Owned(Fx.Parse("{\"appid\":1245620,\"name\":\"ELDEN RING\"}")).ExternalId;
        Assert.Equal(fromStore, fromLibrary);
        Assert.Equal("steam:1245620", fromLibrary);   // an id that comes from Steam starts with "steam:"
    }

    [Fact]
    public void Artwork_urls_are_steams_own_cdn_by_app_id()
    {
        var e = SteamMapper.Owned(Game(10));
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/10/library_600x900.jpg", e.PosterUrl);
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg", e.BackdropUrl);
    }

    [Fact]
    public void A_game_with_no_name_is_still_imported_under_its_id()
    {
        var e = SteamMapper.Owned(Fx.Parse("{\"appid\":99,\"playtime_forever\":1}"));
        Assert.Equal("Steam app 99", e.Title);
    }

    [Fact]
    public void A_wishlist_entry_is_named_from_the_store_and_keeps_both_records()
    {
        var item = Fx.Json("wishlist.json").GetProperty("response").GetProperty("items")[0];
        var details = Fx.Json("appdetails-1245620.json").GetProperty("1245620").GetProperty("data");
        var e = SteamMapper.Wishlisted(item, details);
        Assert.True(e.IsWishlist);
        Assert.Null(e.PlaytimeMinutes);
        Assert.Equal("ELDEN RING", e.Title);
        Assert.Equal(2022, e.Year);
        Assert.NotNull(e.AcquiredAt);             // the date it was wishlisted
        using var raw = JsonDocument.Parse(e.RawJson);
        Assert.True(raw.RootElement.TryGetProperty("wishlist", out _));
        Assert.True(raw.RootElement.TryGetProperty("store", out _));
    }

    [Theory]
    [InlineData("Feb 25, 2022", 2022)]
    [InlineData("2020", 2020)]
    [InlineData("Q3 2025", 2025)]
    [InlineData("Coming soon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_year_comes_out_of_whatever_steam_calls_a_date(string? date, int? year) => Assert.Equal(year, SteamMapper.YearOf(date));

    [Fact]
    public void The_store_record_fills_every_field_the_plugin_declares()
    {
        var d = Fx.Json("appdetails-1245620.json").GetProperty("1245620").GetProperty("data");
        var m = SteamMapper.Details("1245620", d, null);
        Assert.Equal("ELDEN RING", m.Title);
        Assert.Equal(2022, m.Year);
        Assert.False(string.IsNullOrWhiteSpace(m.Overview));
        Assert.Equal("https://store.steampowered.com/app/1245620/", m.SourceUrl);
        Assert.Contains("RPG", m.Genres);
        Assert.NotEmpty(m.Tags);
        Assert.Contains(m.Cast, c => c.Name.Contains("FromSoftware") && c.Role == "Developer");
        Assert.Contains(m.Crew, c => c.Job == "Publisher");
        Assert.False(string.IsNullOrEmpty(m.BackdropUrl));
        Assert.Contains(m.AdditionalImages, i => i.Type == "Screenshot");
        Assert.All(m.AdditionalImages, i => Assert.StartsWith("https://", i.Url));
        Assert.Equal(m.AdditionalImages.Count, m.AdditionalImages.Select(i => i.Url).Distinct().Count());
        Assert.NotNull(m.Rating);                 // metacritic / 10
    }

    [Fact]
    public void The_complete_store_record_is_kept_under_the_plugins_key()
    {
        var d = Fx.Json("appdetails-1245620.json").GetProperty("1245620").GetProperty("data");
        var m = SteamMapper.Details("1245620", d, null);
        var steam = m.ExtendedData!.Value.GetProperty("steam").GetProperty("appdetails");
        foreach (var f in new[] { "detailed_description", "pc_requirements", "supported_languages", "package_groups", "dlc", "content_descriptors", "movies", "screenshots", "price_overview" })
            Assert.True(steam.TryGetProperty(f, out _), f);
    }

    [Fact]
    public void Achievements_merge_the_schema_the_owners_progress_and_global_rarity()
    {
        var a = SteamMapper.Achievements(Fx.Json("schema-553850.json"), Fx.Json("player-553850.json"), Fx.Json("global-553850.json"))!;
        var total = a["total"]!.GetValue<int>();
        Assert.True(total > 0);
        Assert.Equal(total, a["list"]!.AsArray().Count);
        Assert.True(a["unlocked"]!.GetValue<int>() <= total);
        var first = a["list"]![0]!;
        Assert.NotNull(first["name"]);
        Assert.NotNull(first["globalPercent"]);
        Assert.NotNull(first["achieved"]);
    }

    [Fact]
    public void A_missing_part_leaves_that_part_out_instead_of_failing()
    {
        var schemaOnly = SteamMapper.Achievements(Fx.Json("schema-553850.json"), null, null)!;
        Assert.Null(schemaOnly["list"]![0]!["achieved"]);
        Assert.Null(schemaOnly["list"]![0]!["globalPercent"]);
        Assert.Null(SteamMapper.Achievements(null, null, null));
    }

    [Fact]
    public void The_account_record_keeps_profile_level_badges_and_recent_play_whole()
    {
        var info = SteamMapper.Account(Fx.Json("summary.json"), Fx.Json("level.json"), Fx.Json("badges.json"), Fx.Json("recent.json"));
        Assert.Equal("TestPlayer", info.DisplayName);
        using var raw = JsonDocument.Parse(info.RawJson);
        Assert.Equal(13, raw.RootElement.GetProperty("level").GetProperty("response").GetProperty("player_level").GetInt32());
        Assert.True(raw.RootElement.GetProperty("badges").GetProperty("response").GetProperty("badges").GetArrayLength() > 0);
        Assert.True(raw.RootElement.TryGetProperty("recentlyPlayed", out _));
    }
}
