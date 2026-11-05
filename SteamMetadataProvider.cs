using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Steam;

/// <summary>
/// The Steam store half of the plugin: everything the store knows about a game (description, genres, categories, developers,
/// publishers, release date, screenshots, trailers, requirements, DLC, ratings) and the owner's achievements for it. Needs no
/// other plugin; for a game imported from the library the id is already known and no search happens.
/// </summary>
public sealed class SteamMetadataProvider : IMetadataProvider
{
    public const string TypeName = "game";

    public string PluginId => SteamHub.PluginId;
    public string Name => "Steam";
    public string Version => typeof(SteamMetadataProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    public string Author => "Chronicle Contributors";

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName = TypeName,
            // Identical to the IMDb and IGDB declarations of this type: Chronicle merges them by name.
            DisplayName = "Video Games", HierarchyLevels = 1, HierarchyLabels = ["Game"],
            InteractionVerb = "played", ProgressUnit = "percent", IsTrackable = true,
            CastHeading = "Developers", CastAreActors = false,
            Aliases = ["games", "videogame", "video_game"],
            EnrichOnly = false, DefaultPriority = 20,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url", "rating", "genres", "tags", "cast", "crew"],
        },
    ];

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["steam:"];

    public PluginSettingsSchema GetSettingsSchema() => SteamSettingsSchema.Build();

    public void Configure(IReadOnlyDictionary<string, string> settings) => SteamHub.Configure(settings);

    // ── search ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        if (context.MediaTypeName is not null && !TypeFits(context.MediaTypeName)) return [];

        // An item Steam imported already carries its app id: no search, no guessing.
        if (KnownAppId(context.KnownExternalIds) is { } known)
        {
            var meta = await FetchAsync(known, ct);
            return meta is null ? [] : [new ScoredCandidate(meta, 100, "Steam app id already known") { IdentifierMatch = true }];
        }

        var term = (context.Name ?? "").Trim();
        if (term.Length == 0) return [];
        var found = await SteamApi.StoreSearchAsync(term, ct);
        var list = new List<ScoredCandidate>();
        foreach (var item in found.Objs("items").Take(10))
        {
            if (item.Long("id") is not { } id) continue;
            var title = item.Str("name") ?? "";
            var score = Score(term, title);
            list.Add(new ScoredCandidate(new MediaMetadata
            {
                ExternalId = id.ToString(), Source = "steam", Title = title,
                PosterUrl = SteamMapper.PosterUrl(id.ToString()), SourceUrl = SteamMapper.StoreUrl(id.ToString()),
            }, score, score == 100 ? "exact title" : "similar title"));
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }

    private static int Score(string wanted, string title)
    {
        string N(string s) => Regex.Replace(s.ToLowerInvariant().Replace("™", "").Replace("®", ""), "[^a-z0-9]+", " ").Trim();
        var a = N(wanted); var b = N(title);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 100;
        if (b.StartsWith(a) || a.StartsWith(b)) return 75;
        return b.Contains(a) || a.Contains(b) ? 60 : 30;
    }

    private static bool TypeFits(string? t) => t is null or TypeName or "games" or "videogame" or "video_game";

    // ── one game ─────────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        var id = ParseAppId(externalId) ?? throw new InvalidOperationException($"'{externalId}' is not a Steam app id.");
        // Null here means Steam lists nothing for the app (delisted, or never a store page): that is about this item, not an outage.
        return await FetchAsync(id, ct) ?? throw new InvalidOperationException($"Steam no longer lists app {id}.");
    }

    private static async Task<MediaMetadata?> FetchAsync(string appId, CancellationToken ct)
    {
        var d = await SteamApi.AppDetailsAsync(appId, ct);
        if (d is null) return null;

        JsonObject? ach = null;
        if (d.Value.Obj("achievements")?.Long("total") is > 0 && SteamHub.Current.HasCredentials)
            ach = await AchievementsAsync(appId, ct);

        return SteamMapper.Details(appId, d.Value, ach);
    }

    /// <summary>The owner's progress for one game. Anything missing (private stats, no schema) leaves that part out; a pause or a rejected key is passed up.</summary>
    private static async Task<JsonObject?> AchievementsAsync(string appId, CancellationToken ct)
    {
        var steamId = await SteamApi.ResolveAccountAsync(ct);
        var schema = await SteamApi.AchievementSchemaAsync(appId, ct);
        var player = steamId is null ? null : await SteamApi.PlayerAchievementsAsync(steamId, appId, ct);
        var global = await SteamApi.GlobalAchievementPercentagesAsync(appId, ct);
        return SteamMapper.Achievements(schema, player, global);
    }

    internal static string? ParseAppId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        var m = Regex.Match(t, "store\\.steampowered\\.com/app/(\\d{1,10})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        if (t.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) t = t[6..].Trim();
        return t.Length is > 0 and <= 10 && t.All(char.IsAsciiDigit) && t != "0" ? t : null;
    }

    private static string? KnownAppId(IReadOnlyDictionary<string, string>? known) =>
        known is not null && known.TryGetValue("steam", out var v) ? ParseAppId(v) : null;

    // ── images and health ────────────────────────────────────────────────────

    private static readonly HashSet<string> ImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.akamai.steamstatic.com", "shared.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com",
        "steamcdn-a.akamaihd.net", "shared.cloudflare.steamstatic.com", "avatars.steamstatic.com", "video.akamai.steamstatic.com",
    };

    private static readonly HttpClient ImageHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort || !ImageHosts.Contains(uri.Host))
            throw new ArgumentException("This plugin fetches images only from Steam's image servers.");
        using var resp = await ImageHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > 25L * 1024 * 1024) throw new InvalidDataException("The image is larger than 25 MB.");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try { return await SteamApi.AppDetailsAsync("10", ct) is not null; }
        catch (HttpRequestException) { return false; }
    }
}
