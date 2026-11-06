using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Steam;

/// <summary>What the owner typed, read once: the key, the account, the language and the store country.</summary>
internal sealed record Settings(string ApiKey, string SteamIdInput, string Language, string Country)
{
    public const string KeyApiKey = "api_key", KeySteamId = "steam_id", KeyLanguage = "language", KeyCountry = "country";

    public static readonly Settings Default = new("", "", "english", "us");

    public bool HasCredentials => ApiKey.Length > 0 && SteamIdInput.Length > 0;

    public static Settings From(IReadOnlyDictionary<string, string>? s)
    {
        string Get(string k, string d) => s is not null && s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : d;
        // A key is 32 hex characters; anything with control characters or whitespace inside is a paste accident and is refused, not sent.
        var key = Get(KeyApiKey, "");
        if (key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) key = "";
        var lang = Get(KeyLanguage, "english").ToLowerInvariant();
        if (!Regex.IsMatch(lang, "^[a-z]{2,20}(-[a-z]{2,10})?$")) lang = "english";
        var cc = Get(KeyCountry, "us").ToLowerInvariant();
        if (!Regex.IsMatch(cc, "^[a-z]{2}$")) cc = "us";
        return new Settings(key, Get(KeySteamId, ""), lang, cc);
    }

    /// <summary>
    /// What the owner pasted into the account box, as the numeric Steam ID when that is what it is: a bare number or a /profiles/ address. No length is assumed.
    /// A vanity name (a bare word or an /id/ address) comes back as <c>Vanity</c> and is resolved through Steam.
    /// </summary>
    public (string? Id, string? Vanity) ParseAccount()
    {
        var t = SteamIdInput.Trim().TrimEnd('/');
        // A numeric Steam ID is a long number; its length is not assumed. A custom address name can be all digits, but not this many.
        if (Regex.IsMatch(t, "^\\d{15,25}$")) return (t, null);
        var m = Regex.Match(t, "steamcommunity\\.com/profiles/(\\d{5,25})", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, null);
        m = Regex.Match(t, "steamcommunity\\.com/id/([A-Za-z0-9_\\-]{2,64})", RegexOptions.IgnoreCase);
        if (m.Success) return (null, m.Groups[1].Value);
        if (Regex.IsMatch(t, "^[A-Za-z0-9_\\-]{2,64}$")) return (null, t);
        return (null, null);
    }
}

/// <summary>
/// The state both halves of the plugin share. Chronicle makes one instance per interface (the importer and the metadata
/// provider are separate objects with separate Configure calls), so the pacing, the cool-down and the rejected-key latch live
/// here, once per plugin, and neither half can exceed Steam's limits or miss the other's failure.
/// </summary>
internal static class SteamHub
{
    public const string PluginId = "chronicle.plugin.steam";

    private static readonly object Gate = new();
    private static Settings _settings = Settings.Default;
    private static string? _resolvedId;
    private static string? _vanityResolvedFor;

    // The web API is generous (a day's allowance is large); the store's documented ceiling is about 200 a 5 minutes, which is one every 1.5 s.
    public static readonly Limiter Api = new(4, 4, () => DateTimeOffset.UtcNow, Task.Delay);
    public static readonly Limiter Store = new(0.6, 2, () => DateTimeOffset.UtcNow, Task.Delay);
    public static readonly Breaker ApiBreaker = new(() => DateTimeOffset.UtcNow, Random.Shared.NextDouble);
    public static readonly Breaker StoreBreaker = new(() => DateTimeOffset.UtcNow, Random.Shared.NextDouble);

    /// <summary>
    /// The art references Steam puts in a library record (the game's icon and its library capsule are hashed paths, not addresses), by app id,
    /// from the last library import. The store half uses them when it builds a game's images, so what the library record points at is shown.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Icon, string? Capsule)> LibraryImages = new();

    public static Settings Current { get { lock (Gate) return _settings; } }

    public static void Configure(IReadOnlyDictionary<string, string>? values)
    {
        var s = Settings.From(values);
        lock (Gate)
        {
            _settings = s;
            _badKeyHash = null;                       // new settings are a fresh start for the key latch
            _vanityResolvedFor = null; _resolvedId = null;
        }
    }

    /// <summary>Tests only: a clean slate (no cool-down, no latch, fast pacing).</summary>
    internal static void ResetForTests()
    {
        Configure(new Dictionary<string, string>());
        Api.Configure(1000, 8); Store.Configure(1000, 8);
        ApiBreaker.Success(); StoreBreaker.Success(); ClearLatch();
    }

    // ── rejected-key latch ───────────────────────────────────────────────────
    // Steam answers a bad key and a missing key identically and says "retrying will not help". One rejection stops every later call
    // (for up to 15 minutes, or until the settings change) instead of one rejected call per item.
    private static string? _badKeyHash;
    private static DateTimeOffset _badKeyUntil;
    private static readonly TimeSpan LatchFor = TimeSpan.FromMinutes(15);

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static void LatchKey()
    {
        lock (Gate) { _badKeyHash = Hash(_settings.ApiKey); _badKeyUntil = DateTimeOffset.UtcNow + LatchFor; }
    }

    public static bool KeyIsLatched()
    {
        lock (Gate) return _badKeyHash is not null && _badKeyHash == Hash(_settings.ApiKey) && DateTimeOffset.UtcNow < _badKeyUntil;
    }

    public static void ClearLatch() { lock (Gate) _badKeyHash = null; }

    // ── account id (a vanity name is resolved once) ──────────────────────────
    public static string? CachedId(string vanity)
    {
        lock (Gate) return _vanityResolvedFor == vanity ? _resolvedId : null;
    }

    public static void RememberId(string vanity, string id)
    {
        lock (Gate) { _vanityResolvedFor = vanity; _resolvedId = id; }
    }

    /// <summary>Removes the key from any text before it can reach a log or an error.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var key = Current.ApiKey;
        if (key.Length >= 8) text = text.Replace(key, "***", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(text, "(?i)([?&]key=)[^&\\s\"]+", "$1***");
    }
}
