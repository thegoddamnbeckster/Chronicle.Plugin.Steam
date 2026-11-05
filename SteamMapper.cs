using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Steam;

/// <summary>Turns Steam's answers into Chronicle's shapes. Nothing is dropped: whole records ride along as raw JSON.</summary>
internal static class SteamMapper
{
    private const string Cdn = "https://cdn.akamai.steamstatic.com/steam/apps";

    public static string PosterUrl(string appId) => $"{Cdn}/{appId}/library_600x900.jpg";
    public static string HeaderUrl(string appId) => $"{Cdn}/{appId}/header.jpg";
    public static string StoreUrl(string appId) => $"https://store.steampowered.com/app/{appId}/";

    /// <summary>
    /// The id Chronicle stores under the "steam" source, from either half of this plugin: the app id with the platform prefix ("steam:1245620").
    /// One form everywhere. The import and the metadata half once disagreed (prefixed against bare), which made one game two items.
    /// </summary>
    public static string ExternalId(string appId) => "steam:" + appId;

    public static int? YearOf(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        var m = Regex.Match(date, "\\b(19[7-9]\\d|20\\d\\d)\\b");
        return m.Success ? int.Parse(m.Value) : null;
    }

    // ── library ──────────────────────────────────────────────────────────────

    /// <summary>One owned game, from GetOwnedGames. Steam's last-played of 0 means never, and is never turned into a date.</summary>
    public static ImportedLibraryEntry Owned(JsonElement g)
    {
        var appId = (g.Long("appid") ?? 0).ToString();
        var name = g.Str("name") is { Length: > 0 } n ? n : $"Steam app {appId}";
        var last = g.Long("rtime_last_played") is > 0 and var t ? DateTimeOffset.FromUnixTimeSeconds(t) : (DateTimeOffset?)null;
        return new ImportedLibraryEntry(
            ExternalId(appId), new Dictionary<string, string>(), "game", name, null, "PC", "steam",
            (int?)(g.Long("playtime_forever") ?? 0), last, null, false, false, g.GetRawText(),
            PosterUrl: PosterUrl(appId), BackdropUrl: HeaderUrl(appId));
    }

    /// <summary>One wishlist entry (appid, priority, date added) with the store's record that gives it a name.</summary>
    public static ImportedLibraryEntry Wishlisted(JsonElement item, JsonElement details)
    {
        var appId = (item.Long("appid") ?? 0).ToString();
        var raw = new JsonObject { ["wishlist"] = JsonNode.Parse(item.GetRawText()), ["store"] = JsonNode.Parse(details.GetRawText()) };
        var added = item.Long("date_added") is > 0 and var t ? DateTimeOffset.FromUnixTimeSeconds(t) : (DateTimeOffset?)null;
        var tags = new List<string>();
        if (details.Str("type") is { Length: > 0 } ty && ty != "game") tags.Add(ty);
        return new ImportedLibraryEntry(
            ExternalId(appId), new Dictionary<string, string>(), "game", details.Str("name") ?? $"Steam app {appId}",
            YearOf(details.Obj("release_date")?.Str("date")), "PC", "steam", null, null, added, false, true, raw.ToJsonString(),
            Tags: tags.Count > 0 ? tags : null, PosterUrl: PosterUrl(appId), BackdropUrl: HeaderUrl(appId),
            Overview: details.Str("short_description"));
    }

    // ── metadata ─────────────────────────────────────────────────────────────

    public static MediaMetadata Details(string appId, JsonElement d, JsonObject? achievements)
    {
        var m = new MediaMetadata
        {
            ExternalId = ExternalId(appId),
            Source = "steam",
            Title = d.Str("name") ?? $"Steam app {appId}",
            Year = YearOf(d.Obj("release_date")?.Str("date")),
            Overview = d.Str("short_description") is { Length: > 0 } sd ? sd : StripHtml(d.Str("about_the_game")),
            SourceUrl = StoreUrl(appId),
            PosterUrl = PosterUrl(appId),
            BackdropUrl = FirstScreenshot(d) ?? d.Str("background_raw") ?? d.Str("header_image"),
        };

        m.Genres = d.Names("genres", "description");
        var tags = d.Names("categories", "description");
        if (d.Str("type") is { Length: > 0 } ty && ty != "game") tags.Insert(0, ty);
        if (d.Obj("platforms") is { } pl)
            foreach (var p in new[] { "windows", "mac", "linux" })
                if (pl.Bool(p) == true) tags.Add("Platform: " + (p == "mac" ? "macOS" : char.ToUpper(p[0]) + p[1..]));
        if (d.Str("controller_support") is { Length: > 0 } cs) tags.Add("Controller: " + cs);
        if (d.Bool("is_free") == true) tags.Add("Free to play");
        m.Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (d.Obj("metacritic")?.Long("score") is { } mc and > 0) m.Rating = Math.Round(mc / 10.0, 1);

        foreach (var dev in d.Strs("developers")) m.Cast.Add(new CastMember(dev, "Developer"));
        foreach (var pub in d.Strs("publishers")) m.Crew.Add(new CrewMember(pub, "Publisher"));

        AddImage(m, d.Str("header_image"), "Header");
        AddImage(m, d.Str("capsule_imagev5") ?? d.Str("capsule_image"), "Capsule");
        AddImage(m, d.Str("background_raw") ?? d.Str("background"), "Background");
        AddImage(m, $"{Cdn}/{appId}/library_hero.jpg", "Library hero");
        AddImage(m, $"{Cdn}/{appId}/logo.png", "Logo");
        foreach (var s in d.Objs("screenshots"))
            AddImage(m, s.Str("path_full"), "Screenshot", s.Str("path_thumbnail"));
        foreach (var v in d.Objs("movies"))
            AddImage(m, v.Str("thumbnail"), "Trailer thumbnail", v.Str("thumbnail"));

        // The whole store record, and the achievements, under one key: the item page's plugin box shows it, nothing is dropped.
        var ext = new JsonObject { ["appdetails"] = JsonNode.Parse(d.GetRawText()) };
        if (achievements is not null) ext["achievements"] = achievements;
        m.ExtendedData = JsonSerializer.SerializeToElement(new JsonObject { ["steam"] = ext });
        return m;
    }

    private static string? FirstScreenshot(JsonElement d) => d.Objs("screenshots").Select(s => s.Str("path_full")).FirstOrDefault(u => !string.IsNullOrEmpty(u));

    private static void AddImage(MediaMetadata m, string? url, string type, string? thumb = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        url = url.Split('?')[0];
        if (m.AdditionalImages.Any(i => i.Url == url)) return;
        m.AdditionalImages.Add(new AdditionalImage { Url = url, Type = type, ThumbnailUrl = thumb?.Split('?')[0] });
    }

    private static string? StripHtml(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : System.Net.WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")).Trim();

    // ── achievements ─────────────────────────────────────────────────────────

    /// <summary>The schema, the owner's progress and the global rarity merged per achievement. Missing parts are simply absent.</summary>
    public static JsonObject? Achievements(JsonElement? schema, JsonElement? player, JsonElement? global)
    {
        var defs = schema?.Obj("game")?.Obj("availableGameStats")?.Objs("achievements").ToList() ?? [];
        var mine = player?.Obj("playerstats")?.Objs("achievements").ToList() ?? [];
        var pct = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in global?.Obj("achievementpercentages")?.Objs("achievements") ?? [])
            if (a.Str("name") is { } n) pct[n] = a.TryGet("percent", out var pv) ? (pv.ValueKind == JsonValueKind.Number ? pv.GetDouble() : double.TryParse(pv.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0) : 0;
        var progress = mine.Where(a => a.Str("apiname") is not null).ToDictionary(a => a.Str("apiname")!, a => a, StringComparer.OrdinalIgnoreCase);

        if (defs.Count == 0 && progress.Count == 0) return null;

        var list = new JsonArray();
        foreach (var d in defs)
        {
            var key = d.Str("name") ?? "";
            var o = new JsonObject
            {
                ["id"] = key, ["name"] = d.Str("displayName"), ["description"] = d.Str("description"),
                ["hidden"] = d.Long("hidden") == 1, ["icon"] = d.Str("icon"), ["iconGray"] = d.Str("icongray"),
            };
            if (pct.TryGetValue(key, out var p)) o["globalPercent"] = p;
            if (progress.TryGetValue(key, out var pr))
            {
                o["achieved"] = pr.Long("achieved") == 1;
                if (pr.Long("unlocktime") is > 0 and var ut) o["unlockedAt"] = DateTimeOffset.FromUnixTimeSeconds(ut).ToString("O");
            }
            list.Add(o);
        }
        // Achievements the owner holds that the schema did not list are still kept.
        foreach (var (k, pr) in progress)
            if (!defs.Any(d => string.Equals(d.Str("name"), k, StringComparison.OrdinalIgnoreCase)))
                list.Add(new JsonObject { ["id"] = k, ["name"] = pr.Str("name"), ["description"] = pr.Str("description"), ["achieved"] = pr.Long("achieved") == 1 });

        var unlocked = progress.Values.Count(a => a.Long("achieved") == 1);
        return new JsonObject
        {
            ["total"] = list.Count, ["unlocked"] = unlocked, ["gameName"] = player?.Obj("playerstats")?.Str("gameName"),
            ["complete"] = list.Count > 0 && unlocked >= list.Count, ["list"] = list,
        };
    }

    // ── account ──────────────────────────────────────────────────────────────

    public static ImportedAccountInfo Account(JsonElement summary, JsonElement level, JsonElement badges, JsonElement recent)
    {
        var player = summary.Obj("response")?.Objs("players").FirstOrDefault() ?? default;
        var root = new JsonObject
        {
            ["profile"] = player.ValueKind == JsonValueKind.Object ? JsonNode.Parse(player.GetRawText()) : null,
            ["level"] = JsonNode.Parse(level.GetRawText()),
            ["badges"] = JsonNode.Parse(badges.GetRawText()),
            ["recentlyPlayed"] = JsonNode.Parse(recent.GetRawText()),
        };
        return new ImportedAccountInfo(player.Str("personaname") ?? "Steam account", root.ToJsonString());
    }
}
