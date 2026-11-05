using System.Reflection;
using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Steam;

/// <summary>
/// The Steam account half of the plugin: what the owner HAS. Their whole library (every owned app with playtime and last played),
/// their wishlist, and the account's level and badges. It plays nothing and pretends nothing: a game never started arrives as
/// "owned, 0 minutes", never as a play. Works with no other plugin installed.
/// </summary>
public sealed class SteamImportProvider : IImportProvider
{
    public string PluginId => SteamHub.PluginId;
    public string Name => "Steam";
    public string Version => typeof(SteamImportProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    public string Author => "Chronicle Contributors";
    public string Description => "Imports your Steam library (every game and app you own, with hours played and last played), your wishlist, level and badges.";

    public PluginSettingsSchema GetSettingsSchema() => SteamSettingsSchema.Build();

    public void Configure(IReadOnlyDictionary<string, string> settings) => SteamHub.Configure(settings);

    // Steam has no sign-in step: a Web API key and an account id are the whole of it.
    public Task<DeviceAuthStart> StartAuthAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("Steam needs no sign-in: enter your Web API key and Steam account in the plugin's settings.");

    public Task<DeviceAuthPollResult> PollAuthAsync(string pollCode, CancellationToken ct = default) =>
        throw new InvalidOperationException("Steam needs no sign-in: enter your Web API key and Steam account in the plugin's settings.");

    /// <summary>"Authenticated" means configured and not rejected: the host refuses to sync a plugin that says no.</summary>
    public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        Task.FromResult(SteamHub.Current.HasCredentials && !SteamHub.KeyIsLatched());

    public ImportCapabilities GetCapabilities() => new(SupportsHistory: false, SupportsRatings: false, SupportsWatchlist: false, RequiresDeviceAuth: false)
    {
        SupportsLibrary = true,
    };

    public Task<List<ImportedWatchEvent>> GetWatchHistoryAsync(DateTimeOffset? since = null, CancellationToken ct = default) => Task.FromResult(new List<ImportedWatchEvent>());
    public Task<List<ImportedRating>> GetRatingsAsync(CancellationToken ct = default) => Task.FromResult(new List<ImportedRating>());
    public Task<List<ImportedWatchlistEntry>> GetWatchlistAsync(CancellationToken ct = default) => Task.FromResult(new List<ImportedWatchlistEntry>());

    public async Task<List<ImportedLibraryEntry>> GetLibraryAsync(CancellationToken ct = default)
    {
        var steamId = await SteamApi.ResolveAccountAsync(ct) ?? throw Fail.Auth(AccountNotFound());
        await EnsureVisibleAsync(steamId, ct);

        var owned = await SteamApi.OwnedGamesAsync(steamId, ct);
        var resp = owned.Obj("response");
        // Steam answers a private "game details" setting with an empty response, not an error. That must never read as "no games".
        if (resp is null || (!resp.Value.Has("games") && resp.Value.Long("game_count") is null or > 0))
            throw new InvalidOperationException(
                "Steam returned no game list. In Steam, open Edit Profile, then Privacy Settings, and set Game details to Public (at least while Chronicle imports).");

        var entries = new List<ImportedLibraryEntry>();
        var ownedIds = new HashSet<string>();
        foreach (var g in resp.Value.Objs("games"))
        {
            if (g.Long("appid") is not { } id) continue;
            entries.Add(SteamMapper.Owned(g));
            ownedIds.Add(id.ToString());
        }

        entries.AddRange(await WishlistAsync(steamId, ownedIds, ct));
        return entries;
    }

    /// <summary>
    /// Says what to type instead. The commonest slip is the name shown on the profile (the display name, which is also what other sites
    /// call the account), which is not the custom address name Steam can look up; it resolves to nothing.
    /// </summary>
    internal static string AccountNotFound()
    {
        var (_, vanity) = SteamHub.Current.ParseAccount();
        return vanity is null
            ? "That is not a Steam account Steam can find. Enter the number from your profile address (steamcommunity.com/profiles/ followed by a number), or paste the whole address."
            : $"Steam has no profile with the custom address name '{vanity}'. The name shown on a profile is not the same as its custom address name. " +
              "Open your profile in a browser: if the address is like https://steamcommunity.com/profiles/76561198012345678, enter the number at the end (here 76561198012345678); if it is like https://steamcommunity.com/id/yourname, enter that name.";
    }

    private static async Task EnsureVisibleAsync(string steamId, CancellationToken ct)
    {
        var s = await SteamApi.PlayerSummaryAsync(steamId, ct);
        var player = s.Obj("response")?.Objs("players").FirstOrDefault() ?? default;
        if (player.ValueKind != JsonValueKind.Object)
            throw Fail.Auth("Steam does not know that account. Check the Steam ID in the plugin's settings.");
        // 3 = public. Anything else hides the game list from the API.
        if (player.Long("communityvisibilitystate") is { } v and not 3)
            throw new InvalidOperationException(
                "Your Steam profile is not public, so Steam will not list your games. Set the profile and Game details to Public (at least while Chronicle imports).");
    }

    /// <summary>
    /// Wishlist entries carry only an app id, so each is named from the store's record. The store is paced, so a rate limit stops the naming
    /// where it is and the rest are picked up on the next import; nothing already named is lost and nothing is invented.
    /// </summary>
    private static async Task<List<ImportedLibraryEntry>> WishlistAsync(string steamId, HashSet<string> ownedIds, CancellationToken ct)
    {
        var list = new List<ImportedLibraryEntry>();
        JsonElement w;
        try { w = await SteamApi.WishlistAsync(steamId, ct); }
        catch (HttpRequestException) { return list; }   // a wishlist outage must not fail the library

        foreach (var item in w.Obj("response")?.Objs("items") ?? [])
        {
            if (item.Long("appid") is not { } id || ownedIds.Contains(id.ToString())) continue;
            try
            {
                var d = await SteamApi.AppDetailsAsync(id.ToString(), ct);
                if (d is null) continue;   // delisted: nothing names it, so it waits rather than being invented
                // Steam's store also lists its own hardware (Steam Deck, Steam Machine) on a wishlist. That is not a game and does not belong in
                // the Video Games library, so it is left out. This is the one thing the import skips by kind; DLC, demos and soundtracks are kept.
                if (string.Equals(d.Value.Str("type"), "hardware", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(SteamMapper.Wishlisted(item, d.Value));
            }
            catch (HttpRequestException) { break; }
        }
        return list;
    }

    public async Task<ImportedAccountInfo?> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var steamId = await SteamApi.ResolveAccountAsync(ct);
        if (steamId is null) return null;
        var summary = await SteamApi.PlayerSummaryAsync(steamId, ct);
        var level   = await SteamApi.LevelAsync(steamId, ct);
        var badges  = await SteamApi.BadgesAsync(steamId, ct);
        var recent  = await SteamApi.RecentlyPlayedAsync(steamId, ct);
        return SteamMapper.Account(summary, level, badges, recent);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (!SteamHub.Current.HasCredentials) return false;
        try
        {
            var id = await SteamApi.ResolveAccountAsync(ct);
            if (id is null) return false;
            var s = await SteamApi.PlayerSummaryAsync(id, ct);
            return s.Obj("response")?.Objs("players").Any() == true;
        }
        catch (PluginAuthException) { return false; }
    }
}
