using System.Net;
using Chronicle.Plugins;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

/// <summary>How a failure is told to Chronicle decides what happens to the item, so each kind is pinned here.</summary>
[Collection("steam")]
public class ApiTests
{
    private const string Denied = "<html><head><title>Unauthorized</title></head><body><h1>Unauthorized</h1>Access is denied. Retrying will not help. Please verify your <pre>key=</pre> parameter.</body></html>";

    [Fact]
    public async Task A_rejected_key_is_one_auth_failure_and_every_later_call_fails_without_the_network()
    {
        using var net = new Net().Configure().On("GetOwnedGames", Denied, HttpStatusCode.Unauthorized, "text/html");

        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));
        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));
        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.LevelAsync(Fx.Id, default));

        Assert.Equal(1, net.Requests.Count);      // the other two never left the machine
    }

    [Fact]
    public async Task Saving_the_settings_again_lifts_the_latch()
    {
        using var net = new Net().Configure().On("GetOwnedGames", Denied, HttpStatusCode.Forbidden, "text/html");
        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));

        net.On("GetOwnedGames", "{\"response\":{\"game_count\":0}}");
        net.Configure();                          // the owner pasted the key again
        var j = await SteamApi.OwnedGamesAsync(Fx.Id, default);
        Assert.Equal(0, j.GetProperty("response").GetProperty("game_count").GetInt32());
    }

    [Fact]
    public async Task A_game_the_account_does_not_own_is_an_absence_not_a_rejected_key()
    {
        // Measured: Steam answers 403 with JSON ("Profile is not public") for a game that is not in the library. Reading that as a bad key
        // would switch the whole plugin off.
        using var net = new Net().Configure().OnFixture("GetPlayerAchievements", "player-notowned-403.json");
        net.On("GetPlayerAchievements", Fx.Text("player-notowned-403.json"), HttpStatusCode.Forbidden);

        Assert.Null(await SteamApi.PlayerAchievementsAsync(Fx.Id, "1245620", default));
        Assert.False(SteamHub.KeyIsLatched());
        net.On("GetOwnedGames", "{\"response\":{\"game_count\":0}}");
        await SteamApi.OwnedGamesAsync(Fx.Id, default);   // still works
    }

    [Fact]
    public async Task A_game_with_no_stats_answers_400_and_is_an_absence()
    {
        using var net = new Net().Configure().On("GetPlayerAchievements", Fx.Text("player-10-nostats.json"), HttpStatusCode.BadRequest);
        Assert.Null(await SteamApi.PlayerAchievementsAsync(Fx.Id, "10", default));
    }

    [Fact]
    public async Task A_missing_key_is_an_auth_failure_before_any_request()
    {
        using var net = new Net().Configure(key: null);
        await Assert.ThrowsAsync<PluginAuthException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));
        Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task A_rate_limit_is_a_pause_for_everyone_with_no_status_other_than_429()
    {
        using var net = new Net().Configure().On("appdetails", "{}", HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.AppDetailsAsync("10", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(SteamHub.StoreBreaker.IsOpen);

        // the cool-down answers the next call at once, with the same pause, without asking Steam again
        var again = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.AppDetailsAsync("10", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal(1, net.Count("appdetails"));
        // and the web API is not held up by the store's trouble
        Assert.False(SteamHub.ApiBreaker.IsOpen);
    }

    [Fact]
    public async Task The_store_answers_403_when_asked_too_fast_and_that_is_a_rate_limit_too()
    {
        using var net = new Net().Configure().On("appdetails", "<html>Forbidden</html>", HttpStatusCode.Forbidden, "text/html");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.AppDetailsAsync("10", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task A_server_fault_is_a_pause_with_no_status_so_no_retry_is_counted_against_the_item()
    {
        using var net = new Net().Configure().On("GetSteamLevel", "boom", HttpStatusCode.BadGateway, "text/plain");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.LevelAsync(Fx.Id, default));
        Assert.Null(ex.StatusCode);
        Assert.True(SteamHub.ApiBreaker.IsOpen);
    }

    [Fact]
    public async Task A_maintenance_page_where_json_should_be_is_a_pause()
    {
        using var net = new Net().Configure().On("GetSteamLevel", "<html>Down for maintenance</html>", HttpStatusCode.OK, "text/html");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.LevelAsync(Fx.Id, default));
        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task No_answer_at_all_is_a_pause_and_the_message_never_carries_the_key()
    {
        using var net = new Net().Configure();    // nothing routed: the transport throws, with the address in its message
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SteamApi.OwnedGamesAsync(Fx.Id, default));
        Assert.Null(ex.StatusCode);
        Assert.DoesNotContain(Fx.Key, ex.Message);
        Assert.DoesNotContain(Fx.Key, ex.ToString());
    }

    [Fact]
    public async Task A_delisted_app_is_null_not_an_error()
    {
        using var net = new Net().Configure().On("appdetails", Fx.Text("appdetails-delisted-or-empty.json").Replace("\"1\"", "\"777\""));
        Assert.Null(await SteamApi.AppDetailsAsync("777", default));
    }

    [Fact]
    public async Task A_vanity_name_is_resolved_once_and_remembered()
    {
        using var net = new Net().Configure(account: "some_name")
            .On("ResolveVanityURL", "{\"response\":{\"steamid\":\"76561198000000001\",\"success\":1}}");
        Assert.Equal("76561198000000001", await SteamApi.ResolveAccountAsync(default));
        Assert.Equal("76561198000000001", await SteamApi.ResolveAccountAsync(default));
        Assert.Equal(1, net.Count("ResolveVanityURL"));
    }

    [Fact]
    public async Task An_unknown_vanity_name_resolves_to_nothing()
    {
        using var net = new Net().Configure(account: "nobody_here")
            .On("ResolveVanityURL", "{\"response\":{\"success\":42,\"message\":\"No match\"}}");
        Assert.Null(await SteamApi.ResolveAccountAsync(default));
    }

    [Fact]
    public async Task A_numeric_account_is_used_as_typed_with_no_lookup()
    {
        using var net = new Net().Configure();
        Assert.Equal(Fx.Id, await SteamApi.ResolveAccountAsync(default));
        Assert.Empty(net.Requests);
    }

    [Fact]
    public async Task Requests_ask_for_everything_steam_will_give()
    {
        using var net = new Net().Configure().On("GetOwnedGames", "{\"response\":{}}");
        await SteamApi.OwnedGamesAsync(Fx.Id, default);
        var url = net.Requests.Single();
        Assert.Contains("include_appinfo=1", url);
        Assert.Contains("include_played_free_games=1", url);
        Assert.Contains("include_extended_appinfo=1", url);
    }

    [Fact]
    public async Task The_caller_cancelling_is_not_an_outage()
    {
        using var net = new Net().Configure().On("GetSteamLevel", "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SteamApi.LevelAsync(Fx.Id, cts.Token));
        Assert.False(SteamHub.ApiBreaker.IsOpen);
    }
}
