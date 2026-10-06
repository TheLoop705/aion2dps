using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Aion2Dps.Armory.Tests;

internal static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name), Encoding.UTF8);
}

/// <summary>Routes requests by URL substring to canned responses and records every request.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(string Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<Uri> Urls { get; } = [];

    public FakeHandler On(string urlContains, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        _routes.Add((urlContains, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) }));
        return this;
    }

    public FakeHandler OnFixture(string urlContains, string fixture, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json") =>
        On(urlContains, Fixtures.Read(fixture), status, contentType);

    public FakeHandler OnThrow(string urlContains, Exception ex)
    {
        _routes.Add((urlContains, _ => throw ex));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
            Urls.Add(request.RequestUri!);
        }
        var url = request.RequestUri!.AbsoluteUri;
        foreach (var (match, respond) in _routes)
            if (url.Contains(match, StringComparison.Ordinal))
                return Task.FromResult(respond(request));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"status\":404,\"title\":\"Not Found\"}", Encoding.UTF8, "application/problem+json"),
        });
    }

    /// <summary>Handler serving the full Global (EU) fixture set.</summary>
    public static FakeHandler GlobalFixtures() => new FakeHandler()
        .OnFixture("/api/gameinfo/servers", "servers_global_eu.json")
        .OnFixture("/api/gameinfo/classes", "classes_global.json")
        .OnFixture("/api/gameinfo/pcdata", "pcdata_global.json")
        .OnFixture("/search/v2/character", "search_global_eu.json")
        .OnFixture("/api/character/info", "character_info_global.json")
        .OnFixture("/api/character/equipment/item", "item_detail_sockets_kr.json")
        .OnFixture("/api/character/equipment", "equipment_global.json")
        .OnFixture("/api/character/daevanion/detail", "daevanion_detail_global.json")
        .OnFixture("/characters/index", "characters_index_global.html", contentType: "text/html");
}

internal static class TestClients
{
    public static ArmoryClient Create(FakeHandler handler, TimeSpan? interval = null, TimeSpan? cache = null) =>
        new(new HttpClient(handler), new ArmoryClientOptions
        {
            MinRequestInterval = interval ?? TimeSpan.Zero,
            CacheDuration = cache ?? TimeSpan.FromMinutes(10),
        });
}

/// <summary>A [Fact] that only runs when the environment variable AION2DPS_LIVE=1 (it calls the real official site).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AION2DPS_LIVE") != "1")
            Skip = "Live test against the official AION 2 site; set AION2DPS_LIVE=1 to run.";
    }
}
