using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

public class ManifestTests
{
    private static JsonElement Manifest() => Fx.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));

    [Fact]
    public void The_manifest_names_a_real_class_and_the_right_id()
    {
        var m = Manifest();
        Assert.Equal("chronicle.plugin.steam", m.GetProperty("plugin_id").GetString());
        var t = typeof(SteamMetadataProvider).Assembly.GetType(m.GetProperty("entry_type").GetString()!);
        Assert.NotNull(t);
        Assert.Equal(SteamHub.PluginId, m.GetProperty("plugin_id").GetString());
        Assert.Equal(SteamHub.PluginId, new SteamImportProvider().PluginId);
        Assert.Equal(SteamHub.PluginId, new SteamMetadataProvider().PluginId);
    }

    [Fact]
    public void The_manifest_version_matches_the_assembly_and_the_project()
    {
        var v = Manifest().GetProperty("version").GetString();
        Assert.Equal(v, new SteamImportProvider().Version);
        var csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Chronicle.Plugin.Steam.csproj"));
        Assert.Contains($"<Version>{v}</Version>", csproj);
    }

    [Fact]
    public void Every_task_the_manifest_declares_is_one_the_host_can_run()
    {
        // The host runs these by name without any code from the plugin: import-all and delta-sync call the library import, the third enriches.
        string[] wellKnown = ["fetch-missing-metadata", "resync-all-metadata", "import-all", "delta-sync"];
        foreach (var t in Manifest().GetProperty("background_tasks").EnumerateArray())
            Assert.Contains(t.GetProperty("task_id").GetString(), wellKnown);
    }

    [Fact]
    public void The_type_is_declared_exactly_as_the_other_game_plugins_declare_it()
    {
        var t = new SteamMetadataProvider().GetSupportedMediaTypes().Single();
        Assert.Equal("game", t.MediaTypeName);
        Assert.Equal("Video Games", t.DisplayName);
        Assert.Equal("played", t.InteractionVerb);
        Assert.Equal("percent", t.ProgressUnit);
        Assert.Equal(["Game"], t.HierarchyLabels);
        Assert.Equal("Developers", t.CastHeading);
        Assert.Contains("steam:", new SteamMetadataProvider().GetAcceptedCrossRefPrefixes());
    }

    [Fact]
    public void Every_declared_field_is_a_canonical_field_the_host_resolves()
    {
        string[] known = ["title", "overview", "year", "poster_url", "backdrop_url", "logo_url", "banner_url", "thumb_url", "runtime_minutes", "rating", "genres", "cast", "crew", "tags"];
        foreach (var f in new SteamMetadataProvider().GetSupportedMediaTypes().Single().SupportedFields) Assert.Contains(f, known);
        // when the host source is next door, check against its real field map too
        var host = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "Chronicle", "src", "Chronicle.Services", "MetadataResolutionService.cs");
        if (!File.Exists(host)) return;
        var text = File.ReadAllText(host);
        var start = text.IndexOf("FieldMap =", StringComparison.Ordinal);
        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        var hostFields = Regex.Matches(text[start..end], "\\[\"([a-z_]+)\"\\]\\s*=").Select(m => m.Groups[1].Value).ToList();
        foreach (var f in new SteamMetadataProvider().GetSupportedMediaTypes().Single().SupportedFields) Assert.Contains(f, hostFields);
    }

    [Fact]
    public void Every_field_the_plugin_declares_is_one_the_mapper_fills()
    {
        // The Metadata Assignment page lists a plugin for each field it declares; a declared field nothing fills is a lie on that page.
        var d = Fx.Json("appdetails-1245620.json").GetProperty("1245620").GetProperty("data");
        var m = SteamMapper.Details("1245620", d, null, _ => true);
        var filled = new Dictionary<string, bool>
        {
            ["title"] = !string.IsNullOrEmpty(m.Title), ["overview"] = !string.IsNullOrEmpty(m.Overview), ["year"] = m.Year is not null,
            ["poster_url"] = !string.IsNullOrEmpty(m.PosterUrl), ["backdrop_url"] = !string.IsNullOrEmpty(m.BackdropUrl), ["rating"] = m.Rating is not null,
            ["genres"] = m.Genres.Count > 0, ["tags"] = m.Tags.Count > 0, ["cast"] = m.Cast.Count > 0, ["crew"] = m.Crew.Count > 0,
            ["logo_url"] = !string.IsNullOrEmpty(m.LogoUrl), ["banner_url"] = !string.IsNullOrEmpty(m.BannerUrl), ["thumb_url"] = !string.IsNullOrEmpty(m.ThumbUrl),
        };
        foreach (var f in new SteamMetadataProvider().GetSupportedMediaTypes().Single().SupportedFields)
            Assert.True(filled[f], f + " is declared but the mapper leaves it empty");
        foreach (var (f, ok) in filled.Where(kv => kv.Value)) Assert.Contains(f, new SteamMetadataProvider().GetSupportedMediaTypes().Single().SupportedFields);
    }

    [Fact]
    public void The_plugin_stands_alone()
    {
        // No reference to another plugin and no knowledge of one: nothing here may name IGDB, IMDb or Wikipedia.
        var refs = typeof(SteamHub).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, r => r.Contains("IGDB", StringComparison.OrdinalIgnoreCase) || r.Contains("IMDb", StringComparison.OrdinalIgnoreCase) || r.Contains("Wikipedia", StringComparison.OrdinalIgnoreCase));
        var src = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."), "*.cs").Select(File.ReadAllText);
        Assert.DoesNotContain(src, s => Regex.IsMatch(s, "chronicle\\.plugin\\.(igdb|imdb|wikipedia)", RegexOptions.IgnoreCase));
    }
}

/// <summary>Real calls to Steam, only when STEAM_LIVE=1 with STEAM_KEY and STEAM_ID in the environment. Never run in CI; never writes anything.</summary>
[Collection("steam")]
public class LiveTests
{
    private static bool On => Environment.GetEnvironmentVariable("STEAM_LIVE") == "1";

    [SkippableFact]
    public async Task The_real_library_arrives_and_means_what_the_fixtures_say()
    {
        Skip.IfNot(On, "set STEAM_LIVE=1, STEAM_KEY and STEAM_ID to run");
        using var net = new Net();
        SteamApi.Send = (req, ct) => new HttpClient().SendAsync(req, ct);
        SteamHub.Configure(new Dictionary<string, string>
        {
            ["api_key"] = Environment.GetEnvironmentVariable("STEAM_KEY")!, ["steam_id"] = Environment.GetEnvironmentVariable("STEAM_ID")!,
        });
        SteamHub.Store.Configure(0.6, 2);

        var entries = await new SteamImportProvider().GetLibraryAsync();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Title)));
        Assert.Equal(entries.Count, entries.Select(e => e.ExternalId).Distinct().Count());
        Assert.Contains(entries, e => !e.IsWishlist && e.PlaytimeMinutes == 0);   // a real library is mostly unplayed
        Assert.All(entries.Where(e => e.LastPlayedAt is not null), e => Assert.True(e.LastPlayedAt > DateTimeOffset.UnixEpoch));
    }
}
