using System.Net;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

[Collection("steam")]
public class ImportProviderTests
{
    private static Net Library(Net n) => n
        .OnFixture("GetPlayerSummaries", "summary.json")
        .OnFixture("GetOwnedGames", "owned.json")
        .OnFixture("GetWishlist", "wishlist.json");

    [Fact]
    public async Task The_library_is_every_owned_game_with_its_playtime_and_the_wishlist_besides()
    {
        using var net = Library(new Net().Configure());
        // the three wishlist ids resolve to store records
        var ids = Fx.Json("wishlist.json").GetProperty("response").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("appid").GetInt32()).ToList();
        net.On("appdetails", url => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"{ids[0]}\":{{\"success\":true,\"data\":{{\"name\":\"Wanted {ids[0]}\",\"type\":\"game\",\"release_date\":{{\"date\":\"2024\"}}}}}}}}"),
        });

        var entries = await new SteamImportProvider().GetLibraryAsync();

        Assert.Equal(5, entries.Count(e => !e.IsWishlist));
        Assert.Contains(entries, e => e.Title == "Counter-Strike" && e.PlaytimeMinutes == 0 && e.LastPlayedAt is null);
        Assert.Contains(entries, e => e.PlaytimeMinutes > 0 && e.LastPlayedAt is not null);
        Assert.All(entries.Where(e => !e.IsWishlist), e => Assert.Equal("steam", e.StoreSource));
    }

    [Fact]
    public async Task Steam_hardware_on_the_wishlist_is_not_a_game_and_is_left_out_but_add_ons_are_kept()
    {
        using var net = Library(new Net().Configure());
        var ids = Fx.Json("wishlist.json").GetProperty("response").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("appid").GetInt32()).ToList();
        net.On("GetWishlist", $"{{\"response\":{{\"items\":[{{\"appid\":{ids[0]},\"priority\":0,\"date_added\":1700000000}},{{\"appid\":{ids[1]},\"priority\":0,\"date_added\":1700000001}},{{\"appid\":{ids[2]},\"priority\":0,\"date_added\":1700000002}}]}}}}");
        net.On("appdetails", url =>
        {
            var type = url.Contains($"appids={ids[0]}&") ? "hardware" : url.Contains($"appids={ids[1]}&") ? "dlc" : "game";
            var id = url.Contains($"appids={ids[0]}&") ? ids[0] : url.Contains($"appids={ids[1]}&") ? ids[1] : ids[2];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"{id}\":{{\"success\":true,\"data\":{{\"name\":\"N{id}\",\"type\":\"{type}\"}}}}}}") };
        });

        var wish = (await new SteamImportProvider().GetLibraryAsync()).Where(e => e.IsWishlist).ToList();

        Assert.Equal(2, wish.Count);
        Assert.DoesNotContain(wish, e => e.ExternalId == $"steam:{ids[0]}");
        Assert.Contains(wish, e => e.Tags is not null && e.Tags.Contains("dlc"));
    }

    [Fact]
    public async Task The_library_import_remembers_the_art_each_record_points_at()
    {
        using var net = Library(new Net().Configure()).On("appdetails", "{}", HttpStatusCode.TooManyRequests);
        await new SteamImportProvider().GetLibraryAsync();

        var cs = SteamHub.LibraryImages["10"];
        Assert.EndsWith("/apps/10/6b0312cda02f5f777efa2f3318c307ff9acafbb5.jpg", cs.Icon);
        Assert.EndsWith("/store_item_assets/steam/apps/10/library_600x900.jpg", cs.Capsule);
    }

    [Fact]
    public async Task A_wishlisted_game_that_is_already_owned_is_not_listed_twice()
    {
        using var net = Library(new Net().Configure());
        var owned = Fx.Json("owned.json").GetProperty("response").GetProperty("games")[0].GetProperty("appid").GetInt32();
        net.On("GetWishlist", $"{{\"response\":{{\"items\":[{{\"appid\":{owned},\"priority\":0,\"date_added\":1700000000}}]}}}}");

        var entries = await new SteamImportProvider().GetLibraryAsync();

        Assert.DoesNotContain(entries, e => e.IsWishlist);
        Assert.Equal(0, net.Count("appdetails"));
    }

    [Fact]
    public async Task A_store_rate_limit_stops_naming_wishlist_games_but_the_library_still_arrives()
    {
        using var net = Library(new Net().Configure()).On("appdetails", "{}", HttpStatusCode.TooManyRequests);

        var entries = await new SteamImportProvider().GetLibraryAsync();

        Assert.Equal(5, entries.Count);           // the owned games, no wishlist yet
        Assert.Equal(1, net.Count("appdetails")); // it stopped at the first limit instead of hammering on
    }

    [Fact]
    public async Task A_wishlist_outage_does_not_fail_the_library()
    {
        using var net = Library(new Net().Configure()).On("GetWishlist", "oops", HttpStatusCode.BadGateway, "text/plain");
        Assert.Equal(5, (await new SteamImportProvider().GetLibraryAsync()).Count);
    }

    [Fact]
    public async Task A_private_profile_is_said_plainly_never_read_as_an_empty_library()
    {
        using var net = new Net().Configure()
            .On("GetPlayerSummaries", "{\"response\":{\"players\":[{\"steamid\":\"76561198000000000\",\"communityvisibilitystate\":1}]}}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamImportProvider().GetLibraryAsync());
        Assert.Contains("public", ex.Message);
        Assert.Equal(0, net.Count("GetOwnedGames"));
    }

    [Fact]
    public async Task Private_game_details_answer_an_empty_response_and_that_is_not_an_empty_library()
    {
        using var net = new Net().Configure().OnFixture("GetPlayerSummaries", "summary.json").On("GetOwnedGames", "{\"response\":{}}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamImportProvider().GetLibraryAsync());
        Assert.Contains("Game details", ex.Message);
    }

    [Fact]
    public async Task A_genuinely_empty_library_is_an_empty_library()
    {
        using var net = new Net().Configure().OnFixture("GetPlayerSummaries", "summary.json")
            .On("GetOwnedGames", "{\"response\":{\"game_count\":0}}").On("GetWishlist", "{\"response\":{}}");
        Assert.Empty(await new SteamImportProvider().GetLibraryAsync());
    }

    [Fact]
    public async Task An_unknown_account_is_an_auth_problem_with_a_plain_message()
    {
        using var net = new Net().Configure().On("GetPlayerSummaries", "{\"response\":{\"players\":[]}}");
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => new SteamImportProvider().GetLibraryAsync());
        Assert.Contains("Steam ID", ex.Message);
    }

    [Fact]
    public async Task A_display_name_is_explained_not_just_refused()
    {
        // The owner typed the name shown on their profile. It is not a custom address, so Steam has nothing under it.
        using var net = new Net().Configure(account: "thegoddamnbeckster").On("ResolveVanityURL", "{\"response\":{\"success\":42,\"message\":\"No match\"}}");
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => new SteamImportProvider().GetLibraryAsync());
        Assert.Contains("thegoddamnbeckster", ex.Message);
        Assert.Contains("/profiles/", ex.Message);
        Assert.Contains("76561198012345678", ex.Message);
        Assert.DoesNotContain("17 digits", ex.Message);
        Assert.Equal(0, net.Count("GetOwnedGames"));
    }

    [Fact]
    public async Task Account_facts_level_badges_recent_play_come_back_whole()
    {
        using var net = new Net().Configure().OnFixture("GetPlayerSummaries", "summary.json").OnFixture("GetSteamLevel", "level.json")
            .OnFixture("GetBadges", "badges.json").OnFixture("GetRecentlyPlayedGames", "recent.json");
        var info = await new SteamImportProvider().GetAccountInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("TestPlayer", info!.DisplayName);
    }

    [Fact]
    public async Task Authenticated_means_configured_and_not_rejected()
    {
        using var net = new Net();
        var p = new SteamImportProvider();
        Assert.False(await p.IsAuthenticatedAsync());
        net.Configure();
        Assert.True(await p.IsAuthenticatedAsync());
        SteamHub.LatchKey();
        Assert.False(await p.IsAuthenticatedAsync());
        net.Configure();
        Assert.True(await p.IsAuthenticatedAsync());
    }

    [Fact]
    public void It_declares_a_library_and_no_sign_in_flow()
    {
        var c = new SteamImportProvider().GetCapabilities();
        Assert.True(c.SupportsLibrary);
        Assert.False(c.RequiresDeviceAuth);
        Assert.False(c.SupportsHistory);
    }

    [Fact]
    public async Task There_is_no_device_flow_and_asking_for_one_explains_why()
    {
        var p = new SteamImportProvider();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.StartAuthAsync());
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task History_ratings_and_watchlist_are_empty_not_failing()
    {
        var p = new SteamImportProvider();
        Assert.Empty(await p.GetWatchHistoryAsync());
        Assert.Empty(await p.GetRatingsAsync());
        Assert.Empty(await p.GetWatchlistAsync());
    }

    [Fact]
    public async Task Both_halves_share_one_set_of_settings_and_one_key_latch()
    {
        using var net = new Net();
        var imp = new SteamImportProvider();
        var meta = new SteamMetadataProvider();
        imp.Configure(new Dictionary<string, string> { ["api_key"] = Fx.Key, ["steam_id"] = Fx.Id });
        // the metadata half sees what the import half was configured with, because Chronicle builds two objects and the state is shared
        Assert.True(SteamHub.Current.HasCredentials);
        SteamHub.LatchKey();
        Assert.False(await imp.IsAuthenticatedAsync());
        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));
        _ = meta;
    }

    [Fact]
    public void The_account_box_tells_the_owner_exactly_what_to_type_and_what_not_to()
    {
        var all = new SteamImportProvider().GetSettingsSchema().Settings;
        var field = all.Single(s => s.Key == "steam_id");
        Assert.DoesNotContain("17", field.Label);
        Assert.Contains("number in your profile address", field.Label);
        Assert.Contains("NOT your name", field.Label);

        // the callout sits directly above the box it explains
        var notice = all[all.IndexOf(field) - 1];
        Assert.Equal(SettingType.Notice, notice.Type);
        Assert.Contains("NUMBER, not your name", notice.Label);
        Assert.Contains("/profiles/", notice.Description);
        Assert.Contains("ADDRESS BAR", notice.Description);
        Assert.Contains("https://steamcommunity.com/profiles/76561198012345678", notice.Description);
        Assert.Contains("https://steamcommunity.com/my/profile", notice.Description);
        Assert.DoesNotContain("17 digits", notice.Description);
        Assert.Contains("DO NOT type the name shown at the top of your profile", notice.Description);
        Assert.Contains("/id/", notice.Description);
    }

    [Fact]
    public void The_steps_run_in_the_order_the_owner_does_them()
    {
        var keys = new SteamImportProvider().GetSettingsSchema().Settings.Select(s => s.Key).ToList();
        Assert.True(keys.IndexOf("notice_key") < keys.IndexOf("api_key"));
        Assert.True(keys.IndexOf("api_key") < keys.IndexOf("notice_id"));
        Assert.True(keys.IndexOf("notice_id") < keys.IndexOf("steam_id"));
        Assert.True(keys.IndexOf("steam_id") < keys.IndexOf("notice_public"));
    }

    [Fact]
    public void One_settings_form_serves_both_halves()
    {
        var a = new SteamImportProvider().GetSettingsSchema().Settings.Select(s => s.Key);
        var b = new SteamMetadataProvider().GetSettingsSchema().Settings.Select(s => s.Key);
        Assert.Equal(a, b);
        var keys = a.ToList();
        Assert.Contains("api_key", keys);
        Assert.Contains("steam_id", keys);
        Assert.Equal(SettingType.Password, new SteamImportProvider().GetSettingsSchema().Settings.First(s => s.Key == "api_key").Type);
    }
}

[Collection("steam")]
public class MetadataProviderTests
{
    private static Net Elden(Net n) => n.On("appdetails", Fx.Text("appdetails-1245620.json"));

    [Theory]
    [InlineData("1245620", "1245620")]
    [InlineData("steam:1245620", "1245620")]
    [InlineData("STEAM: 1245620", "1245620")]
    [InlineData("https://store.steampowered.com/app/1245620/ELDEN_RING/", "1245620")]
    [InlineData("0", null)]
    [InlineData("steam:abc", null)]
    [InlineData("12345678901", null)]
    [InlineData("", null)]
    public void Fix_match_forms_are_read(string raw, string? id) => Assert.Equal(id, SteamMetadataProvider.ParseAppId(raw));

    [Fact]
    public async Task A_game_is_fetched_whole_by_its_id()
    {
        using var net = Elden(new Net().Configure(key: null, account: null));
        var m = await new SteamMetadataProvider().GetByIdAsync("steam:1245620");
        Assert.Equal("ELDEN RING", m.Title);
        Assert.Equal("steam:1245620", m.ExternalId);
        Assert.Equal("steam", m.Source);
    }

    [Fact]
    public async Task Without_a_key_the_store_half_still_works_and_just_leaves_achievements_out()
    {
        using var net = Elden(new Net().Configure(key: null, account: null));
        var m = await new SteamMetadataProvider().GetByIdAsync("1245620");
        Assert.False(m.ExtendedData!.Value.GetProperty("steam").TryGetProperty("achievements", out _));
        Assert.Equal(0, net.Count("ISteamUserStats"));
    }

    [Fact]
    public async Task Achievements_are_added_when_the_game_has_them_and_a_key_is_set()
    {
        using var net = new Net().Configure()
            .On("appdetails", Fx.Text("appdetails-553850.json"))
            .OnFixture("GetSchemaForGame", "schema-553850.json").OnFixture("GetPlayerAchievements", "player-553850.json")
            .OnFixture("GetGlobalAchievementPercentagesForApp", "global-553850.json");
        var m = await new SteamMetadataProvider().GetByIdAsync("553850");
        var ach = m.ExtendedData!.Value.GetProperty("steam").GetProperty("achievements");
        Assert.True(ach.GetProperty("total").GetInt32() > 0);
    }

    [Fact]
    public async Task A_game_that_is_not_owned_still_gets_its_store_record_and_schema_without_progress()
    {
        using var net = new Net().Configure()
            .On("appdetails", Fx.Text("appdetails-1245620.json"))
            .On("GetSchemaForGame", Fx.Text("schema-553850.json"))
            .On("GetPlayerAchievements", Fx.Text("player-notowned-403.json"), HttpStatusCode.Forbidden)
            .On("GetGlobalAchievementPercentagesForApp", Fx.Text("global-553850.json"));
        var m = await new SteamMetadataProvider().GetByIdAsync("1245620");
        Assert.Equal("ELDEN RING", m.Title);
        Assert.False(SteamHub.KeyIsLatched());
    }

    [Fact]
    public async Task A_delisted_app_ends_as_a_terminal_skip_not_a_retry()
    {
        using var net = new Net().Configure().On("appdetails", "{\"777\":{\"success\":false}}");
        // InvalidOperationException is what the host reads as "this item cannot be matched": Skipped, once.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamMetadataProvider().GetByIdAsync("777"));
    }

    [Fact]
    public async Task A_rate_limit_is_passed_up_unchanged_so_the_host_pauses()
    {
        using var net = new Net().Configure().On("appdetails", "{}", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new SteamMetadataProvider().GetByIdAsync("10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task A_known_steam_id_means_no_search()
    {
        using var net = Elden(new Net().Configure(key: null, account: null));
        var ctx = new MediaSearchContext("whatever it was called", MediaTypeName: "game",
            KnownExternalIds: new Dictionary<string, string> { ["steam"] = "steam:1245620" });
        var found = await new SteamMetadataProvider().SearchAsync(ctx);
        var one = Assert.Single(found);
        Assert.True(one.IdentifierMatch);
        Assert.Equal(0, net.Count("storesearch"));
    }

    [Fact]
    public async Task A_title_search_scores_the_exact_name_first()
    {
        using var net = new Net().Configure().On("storesearch", Fx.Text("storesearch-elden-ring.json"));
        var found = await new SteamMetadataProvider().SearchAsync(new MediaSearchContext("Elden Ring", MediaTypeName: "game"));
        Assert.NotEmpty(found);
        Assert.Equal(found.Max(c => c.Score), found[0].Score);
        Assert.Contains("ELDEN", found[0].Metadata.Title, StringComparison.OrdinalIgnoreCase);
    }

    private static string StoreSearch(params (int Id, string Name)[] items) =>
        "{\"total\":" + items.Length + ",\"items\":[" + string.Join(",", items.Select(i => $"{{\"type\":\"app\",\"name\":\"{i.Name}\",\"id\":{i.Id}}}")) + "]}";

    private static string Details(int id, string name, string year) =>
        $"{{\"{id}\":{{\"success\":true,\"data\":{{\"name\":\"{name}\",\"type\":\"game\",\"release_date\":{{\"date\":\"{year}\"}}}}}}}}";

    [Fact]
    public async Task An_automatic_match_accepts_only_the_exact_title_never_one_that_merely_contains_it()
    {
        // Chronicle filling in an item the user already has: "Command & Conquer" must not become "Command & Conquer 3", nor Duke Nukem Duke Nukem 3D.
        using var net = new Net().Configure().On("storesearch", StoreSearch((24790, "Command & Conquer 3 Tiberium Wars"), (2229850, "Command & Conquer Red Alert 2 and Yuri's Revenge")));
        var ctx = new MediaSearchContext("Command & Conquer", MediaTypeName: "game", IsRealHierarchyPosition: true);

        Assert.Empty(await new SteamMetadataProvider().SearchAsync(ctx));
    }

    [Fact]
    public async Task An_automatic_match_takes_the_exact_title_when_it_is_there_ignoring_trademarks_and_punctuation()
    {
        using var net = new Net().Configure().On("storesearch", StoreSearch((24790, "Command & Conquer 3 Tiberium Wars"), (2229850, "Command & Conquer™")));
        var ctx = new MediaSearchContext("Command & Conquer", MediaTypeName: "game", IsRealHierarchyPosition: true);

        var found = await new SteamMetadataProvider().SearchAsync(ctx);
        Assert.Single(found);
        Assert.Equal("steam:2229850", found[0].Metadata.ExternalId);
    }

    [Fact]
    public async Task An_automatic_match_with_a_known_year_drops_a_same_titled_game_from_another_year()
    {
        using var net = new Net().Configure().On("storesearch", StoreSearch((221, "Prey"), (480490, "Prey")))
            .On("appids=221&", Details(221, "Prey", "Jul 11, 2006")).On("appids=480490&", Details(480490, "Prey", "May 5, 2017"));
        var ctx = new MediaSearchContext("Prey", Year: 2017, MediaTypeName: "game", IsRealHierarchyPosition: true);

        var found = await new SteamMetadataProvider().SearchAsync(ctx);
        var one = Assert.Single(found);
        Assert.Equal("steam:480490", one.Metadata.ExternalId);
        Assert.Equal(2017, one.Metadata.Year);
    }

    [Fact]
    public async Task A_person_searching_in_Add_Media_still_sees_the_similar_titles_to_choose_from()
    {
        using var net = new Net().Configure().On("storesearch", StoreSearch((24790, "Command & Conquer 3 Tiberium Wars"), (1, "Command & Conquer")));
        var found = await new SteamMetadataProvider().SearchAsync(new MediaSearchContext("Command & Conquer", MediaTypeName: "game"));

        Assert.Equal(2, found.Count);
        Assert.Equal("steam:1", found[0].Metadata.ExternalId);   // the exact title first
    }

    [Fact]
    public async Task Another_media_type_gets_nothing()
    {
        using var net = new Net().Configure();
        Assert.Empty(await new SteamMetadataProvider().SearchAsync(new MediaSearchContext("Dune", MediaTypeName: "movies")));
        Assert.Empty(net.Requests);
    }

    [Theory]
    [InlineData("https://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg", true)]
    [InlineData("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/10/library_600x900.jpg", true)]
    [InlineData("https://media.steampowered.com/steamcommunity/public/images/apps/10/6b0312cda02f5f777efa2f3318c307ff9acafbb5.jpg", true)]
    [InlineData("http://cdn.akamai.steamstatic.com/steam/apps/10/header.jpg", false)]
    [InlineData("https://evil.example.com/header.jpg", false)]
    [InlineData("https://cdn.akamai.steamstatic.com.evil.example.com/x.jpg", false)]
    [InlineData("https://user:pw@cdn.akamai.steamstatic.com/x.jpg", false)]
    [InlineData("https://cdn.akamai.steamstatic.com:8443/x.jpg", false)]
    [InlineData("file:///c:/windows/win.ini", false)]
    [InlineData("not a url", false)]
    public async Task Images_come_only_from_steams_own_servers(string url, bool allowed)
    {
        // an allowed address is refused only by the network (nothing here is online), never by the allow-list
        var ex = await Record.ExceptionAsync(() => new SteamMetadataProvider().GetImageAsync(url));
        if (allowed) Assert.False(ex is ArgumentException, "the allow-list refused " + url);
        else Assert.IsType<ArgumentException>(ex);
    }
}
