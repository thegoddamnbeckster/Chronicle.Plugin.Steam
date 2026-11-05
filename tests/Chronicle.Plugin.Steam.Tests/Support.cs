using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.Steam;

namespace Chronicle.Plugin.Steam.Tests;

/// <summary>The plugin keeps process-wide state (pacing, cool-down, key latch), so every test class that touches it runs in this one serial collection.</summary>
[CollectionDefinition("steam", DisableParallelization = true)]
public sealed class SteamCollection { }

internal static class Fx
{
    public const string Key = "0123456789ABCDEF0123456789ABCDEF";
    public const string Id = "76561198000000000";

    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    public static JsonElement Json(string name) { using var d = JsonDocument.Parse(Text(name)); return d.RootElement.Clone(); }
    public static JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
}

/// <summary>Replaces the one place the plugin sends requests. Routes by a substring of the address; records what was asked.</summary>
internal sealed class Net : IDisposable
{
    public readonly List<string> Requests = new();
    private readonly List<(string Part, Func<string, HttpResponseMessage> Reply)> _routes = new();
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _old;

    public Net()
    {
        SteamHub.ResetForTests();
        _old = SteamApi.Send;
        SteamApi.Send = (req, ct) =>
        {
            var url = req.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            foreach (var (part, reply) in _routes)
                if (url.Contains(part, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(reply(url));
            throw new HttpRequestException("nothing routed for " + url);
        };
    }

    public Net Configure(string? key = Fx.Key, string? account = Fx.Id)
    {
        var d = new Dictionary<string, string>();
        if (key is not null) d["api_key"] = key;
        if (account is not null) d["steam_id"] = account;
        SteamHub.Configure(d);
        return this;
    }

    public Net On(string part, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
        On(part, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    public Net OnFixture(string part, string fixture) => On(part, Fx.Text(fixture));

    public Net On(string part, Func<string, HttpResponseMessage> reply) { _routes.Insert(0, (part, reply)); return this; }

    public int Count(string part) { lock (Requests) return Requests.Count(r => r.Contains(part, StringComparison.OrdinalIgnoreCase)); }

    public void Dispose() { SteamApi.Send = _old; SteamHub.ResetForTests(); }
}
