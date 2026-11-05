using System.Net;
using System.Text.Json;

namespace Chronicle.Plugin.Steam;

/// <summary>
/// Every call this plugin makes to Steam. One place sends requests, so the key is redacted in one place, every call is paced
/// by the shared limiter, and every failure is told to Chronicle the way the host expects (see <see cref="Fail"/>).
/// </summary>
internal static class SteamApi
{
    private const string WebApi = "https://api.steampowered.com";
    private const string Store = "https://store.steampowered.com";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Replaceable in tests: send a request, get a response.</summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
        (req, ct) => Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

    private sealed record Policy(Limiter Limiter, Breaker Breaker, bool Keyed, string Name);
    private static readonly Policy ApiKeyed = new(SteamHub.Api, SteamHub.ApiBreaker, true, "Steam Web API");
    private static readonly Policy ApiOpen = new(SteamHub.Api, SteamHub.ApiBreaker, false, "Steam Web API");
    private static readonly Policy StoreOpen = new(SteamHub.Store, SteamHub.StoreBreaker, false, "Steam store");

    /// <summary>How long one call may wait for a pacing slot before it is turned into a pause instead of queueing for minutes.</summary>
    internal static TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(20);

    private static string Q(string s) => Uri.EscapeDataString(s);

    // ── account ──────────────────────────────────────────────────────────────

    /// <summary>The 64-bit id for what the owner typed: used as is, or a vanity name resolved once through Steam.</summary>
    public static async Task<string?> ResolveAccountAsync(CancellationToken ct)
    {
        var (id, vanity) = SteamHub.Current.ParseAccount();
        if (id is not null) return id;
        if (vanity is null) return null;
        if (SteamHub.CachedId(vanity) is { } cached) return cached;
        var j = await GetAsync(ApiKeyed, $"{WebApi}/ISteamUser/ResolveVanityURL/v1/?key={Q(SteamHub.Current.ApiKey)}&vanityurl={Q(vanity)}", ct);
        var r = j.Obj("response");
        if (r?.Long("success") == 1 && r.Value.Str("steamid") is { } sid) { SteamHub.RememberId(vanity, sid); return sid; }
        return null;
    }

    public static Task<JsonElement> PlayerSummaryAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiKeyed, $"{WebApi}/ISteamUser/GetPlayerSummaries/v2/?key={Q(SteamHub.Current.ApiKey)}&steamids={Q(steamId)}", ct);

    // ── library ──────────────────────────────────────────────────────────────

    public static Task<JsonElement> OwnedGamesAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiKeyed, $"{WebApi}/IPlayerService/GetOwnedGames/v1/?key={Q(SteamHub.Current.ApiKey)}&steamid={Q(steamId)}" +
                           "&include_appinfo=1&include_played_free_games=1&include_extended_appinfo=1&format=json", ct);

    public static Task<JsonElement> RecentlyPlayedAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiKeyed, $"{WebApi}/IPlayerService/GetRecentlyPlayedGames/v1/?key={Q(SteamHub.Current.ApiKey)}&steamid={Q(steamId)}&format=json", ct);

    public static Task<JsonElement> LevelAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiKeyed, $"{WebApi}/IPlayerService/GetSteamLevel/v1/?key={Q(SteamHub.Current.ApiKey)}&steamid={Q(steamId)}&format=json", ct);

    public static Task<JsonElement> BadgesAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiKeyed, $"{WebApi}/IPlayerService/GetBadges/v1/?key={Q(SteamHub.Current.ApiKey)}&steamid={Q(steamId)}&format=json", ct);

    public static Task<JsonElement> WishlistAsync(string steamId, CancellationToken ct) =>
        GetAsync(ApiOpen, $"{WebApi}/IWishlistService/GetWishlist/v1/?steamid={Q(steamId)}", ct);

    // ── store ────────────────────────────────────────────────────────────────

    /// <summary>The store's whole record for one app, or null when Steam no longer lists it (a delisted game answers success:false).</summary>
    public static async Task<JsonElement?> AppDetailsAsync(string appId, CancellationToken ct)
    {
        var s = SteamHub.Current;
        var j = await GetAsync(StoreOpen, $"{Store}/api/appdetails?appids={Q(appId)}&cc={s.Country}&l={Q(s.Language)}", ct);
        var entry = j.Obj(appId);
        if (entry is null || entry.Value.Bool("success") != true) return null;
        return entry.Value.Obj("data");
    }

    public static Task<JsonElement> StoreSearchAsync(string term, CancellationToken ct)
    {
        var s = SteamHub.Current;
        return GetAsync(StoreOpen, $"{Store}/api/storesearch/?term={Q(term)}&l={Q(s.Language)}&cc={s.Country}", ct);
    }

    // ── achievements (three calls, all optional: a game without stats answers 400) ──

    public static async Task<JsonElement?> AchievementSchemaAsync(string appId, CancellationToken ct) =>
        await TryAsync(ApiKeyed, $"{WebApi}/ISteamUserStats/GetSchemaForGame/v2/?key={Q(SteamHub.Current.ApiKey)}&appid={Q(appId)}&l={Q(SteamHub.Current.Language)}", ct);

    public static async Task<JsonElement?> PlayerAchievementsAsync(string steamId, string appId, CancellationToken ct) =>
        await TryAsync(ApiKeyed, $"{WebApi}/ISteamUserStats/GetPlayerAchievements/v1/?key={Q(SteamHub.Current.ApiKey)}&steamid={Q(steamId)}&appid={Q(appId)}&l={Q(SteamHub.Current.Language)}", ct);

    public static async Task<JsonElement?> GlobalAchievementPercentagesAsync(string appId, CancellationToken ct) =>
        await TryAsync(ApiOpen, $"{WebApi}/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={Q(appId)}", ct);

    /// <summary>A "this app has no such data" answer (400, 404) is an absence, not a failure.</summary>
    private static async Task<JsonElement?> TryAsync(Policy p, string url, CancellationToken ct)
    {
        try { return await GetAsync(p, url, ct); }
        catch (HttpRequestException h) when (h.Data[Fail.KindKey] is "badrequest") { return null; }
    }

    private static async Task<bool> IsJsonBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException) { return false; }
    }

    // ── the one place requests are sent ──────────────────────────────────────

    private static async Task<JsonElement> GetAsync(Policy p, string url, CancellationToken ct)
    {
        if (p.Keyed)
        {
            if (!SteamHub.Current.HasCredentials) throw Fail.Auth("Enter a Steam Web API key and your Steam account in the Steam plugin's settings.");
            if (SteamHub.KeyIsLatched()) throw Fail.Auth("Steam rejected the API key. Check it in the Steam plugin's settings (saving the settings tries again).");
        }
        p.Breaker.ThrowIfOpen();

        using var lease = await p.Limiter.AcquireAsync(MaxWait, ct).ConfigureAwait(false);
        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            resp = await Send(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // No answer at all: an outage or a timeout, never the item's fault. The message is built here, not copied from ex, because
            // a transport exception can carry the request address and the address carries the key.
            p.Breaker.Trip(false, $"{p.Name} did not answer");
            throw Fail.Pause($"{p.Name} did not answer.", null, "server");
        }

        using (resp)
        {
            var status = resp.StatusCode;
            if (status == HttpStatusCode.OK)
            {
                string body;
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    p.Breaker.Trip(false, $"{p.Name} dropped the connection");
                    throw Fail.Pause($"{p.Name} dropped the connection.", null, "server");
                }
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    p.Breaker.Success();
                    return doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // HTML where JSON should be: Steam's maintenance and error pages. Not this item's fault.
                    p.Breaker.Trip(false, $"{p.Name} sent something other than data");
                    throw Fail.Pause($"{p.Name} sent something other than data.", null, "shape");
                }
            }

            if (status == HttpStatusCode.TooManyRequests || (!p.Keyed && status == HttpStatusCode.Forbidden && p.Name == "Steam store"))
            {
                // The store answers 403 or 429 when it is asked too fast. A pause: no retry is counted against the item.
                var ra = resp.Headers.RetryAfter?.Delta;
                p.Breaker.Trip(true, $"{p.Name} said slow down", ra);
                throw Fail.Pause($"{p.Name} said slow down.", HttpStatusCode.TooManyRequests, "rate");
            }

            if (p.Keyed && status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !await IsJsonBodyAsync(resp, ct))
            {
                // Measured 2026-11-05: a rejected key answers with an HTML page ("Access is denied. Retrying will not help"), but asking for the
                // achievements of a game the account does not own answers 403 with JSON ("Profile is not public"). Only the first is the key.
                // "Access is denied. Retrying will not help." A bad key looks exactly like no key, so one rejection stops the lot.
                SteamHub.LatchKey();
                throw Fail.Auth("Steam rejected the API key. Check it in the Steam plugin's settings.");
            }

            if ((int)status >= 500)
            {
                p.Breaker.Trip(false, $"{p.Name} returned {(int)status}");
                throw Fail.Pause($"{p.Name} returned {(int)status}.", null, "server");
            }

            // A redirect to the login page (a private page), 400 and 404 are answers about this one request.
            throw Fail.Pause($"{p.Name} answered {(int)status} for this request.", status, "badrequest");
        }
    }
}
