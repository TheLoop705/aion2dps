using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Aion2Dps.Armory.Tests;

public class EndpointTests
{
    private readonly ArmoryEndpoints _e = new();

    [Fact]
    public void Global_urls_match_the_official_site()
    {
        Assert.Equal("https://aion2.plaync.com/en-us/api/gameinfo/servers?lang=en-US&region=eu", _e.Servers(ArmoryRegion.Europe)!.AbsoluteUri);
        Assert.Equal("https://api-search.plaync.com/aion2global/search/v2/character?keyword=Luna&page=1&size=40&region=eu&localeInfo=en-US",
            _e.Search(ArmoryRegion.Europe, "Luna", null, 1, 40).AbsoluteUri);
        Assert.Equal("https://api-search.plaync.com/aion2global/search/v2/character?keyword=Luna&serverId=1307&page=2&size=40&region=eu&localeInfo=en-US",
            _e.Search(ArmoryRegion.Europe, "Luna", 1307, 2, 40).AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/api/character/info?region=eu&lang=en-US&characterId=ab%2Bc%3D&serverId=1307",
            _e.CharacterInfo(ArmoryRegion.Europe, 1307, "ab+c=").AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/api/character/equipment?region=nae&lang=en-US&characterId=x&serverId=1101",
            _e.Equipment(ArmoryRegion.NorthAmericaEast, 1101, "x").AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/api/character/equipment/item?id=110530047&enchantLevel=7&characterId=x&serverId=1307&slotPos=1&lang=en-US&region=eu",
            _e.ItemDetail(ArmoryRegion.Europe, 1307, "x", 110530047, 7, 1).AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/api/character/daevanion/detail?region=eu&lang=en-US&characterId=x&serverId=1307&boardId=61",
            _e.Daevanion(ArmoryRegion.Europe, 1307, "x", 61).AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/en-us/characters/index", _e.CharactersPage(ArmoryRegion.Asia).AbsoluteUri);
    }

    [Fact]
    public void Korea_and_taiwan_urls()
    {
        Assert.Null(_e.Servers(ArmoryRegion.Korea));
        Assert.Null(_e.Servers(ArmoryRegion.Taiwan));
        Assert.Equal("https://api-search.plaync.com/aion2/search/v2/character?keyword=%EB%A3%A8%EB%82%98&page=1&size=40",
            _e.Search(ArmoryRegion.Korea, "루나", null, 1, 40).AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/api/character/info?lang=ko&characterId=x&serverId=1015",
            _e.CharacterInfo(ArmoryRegion.Korea, 1015, "x").AbsoluteUri);
        Assert.Equal("https://tw.ncsoft.com/aion2/api/search/character?keyword=a&serverId=1001&race=1&page=1&size=40",
            _e.Search(ArmoryRegion.Taiwan, "a", 1001, 1, 40).AbsoluteUri);
        Assert.Contains("race=2", _e.Search(ArmoryRegion.Taiwan, "a", 2005, 1, 40).Query);
        Assert.Equal("https://tw.ncsoft.com/aion2/api/character/info?lang=zh&characterId=x&serverId=1001",
            _e.CharacterInfo(ArmoryRegion.Taiwan, 1001, "x").AbsoluteUri);
        Assert.Equal("https://aion2.plaync.com/ko-kr/characters/index", _e.CharactersPage(ArmoryRegion.Korea).AbsoluteUri);
    }

    [Fact]
    public void Other_global_language()
    {
        var de = new ArmoryEndpoints("de-DE");
        Assert.Equal("https://aion2.plaync.com/de-de/api/gameinfo/servers?lang=de-DE&region=eu", de.Servers(ArmoryRegion.Europe)!.AbsoluteUri);
    }

    [Theory]
    [InlineData(1301, ArmoryRegion.Europe)]
    [InlineData(2322, ArmoryRegion.Europe)]
    [InlineData(1105, ArmoryRegion.NorthAmericaEast)]
    [InlineData(2203, ArmoryRegion.NorthAmericaWest)]
    [InlineData(1404, ArmoryRegion.SouthAmerica)]
    [InlineData(2508, ArmoryRegion.Asia)]
    [InlineData(1015, null)]
    public void Region_from_server_id(int serverId, ArmoryRegion? expected) =>
        Assert.Equal(expected, ArmoryRegionExtensions.GuessGlobalRegionFromServerId(serverId));
}

public class ClientTests
{
    [Fact]
    public async Task Full_global_flow_with_fixtures()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h);

        var servers = await client.GetServersAsync(ArmoryRegion.Europe);
        Assert.Equal(44, servers.Count);

        var page = await client.SearchCharactersAsync(ArmoryRegion.Europe, "tester");
        Assert.Equal(5, page.Items.Count);
        var hit = page.Items[1];

        var info = await client.GetCharacterAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId);
        Assert.Equal("Tester", info.Profile.Name);
        var eq = await client.GetEquipmentAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId);
        Assert.Equal(18, eq.Items.Count);
        var item = await client.GetItemDetailAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId, eq.Items[0]);
        Assert.Equal(4, item.Manastones.Count);
        var board = await client.GetDaevanionAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId, info.DaevanionBoards[0].Id);
        Assert.Equal(225, board.Nodes.Count);

        // the decoded id is re-encoded exactly once
        var infoUrl = h.Urls.Single(u => u.AbsolutePath == "/api/character/info");
        Assert.Contains("characterId=FAKEcharId01AbCdEf_-0123456789xyzXYZ01%3D", infoUrl.Query);
        Assert.DoesNotContain("%253D", infoUrl.Query);

        // headers
        var req = h.Requests[0];
        Assert.Contains("Mozilla/5.0", req.Headers.UserAgent.ToString());
        Assert.Equal("https://aion2.plaync.com/en-us/characters/index", req.Headers.Referrer!.AbsoluteUri);
        Assert.Equal(6, client.RequestCount);
    }

    [Fact]
    public async Task Responses_are_cached()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h);
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "abc");
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "abc");
        await client.SearchCharactersAsync(ArmoryRegion.Europe, "tester");
        await client.SearchCharactersAsync(ArmoryRegion.Europe, " tester ");
        Assert.Equal(2, h.Requests.Count);

        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "other");
        Assert.Equal(3, h.Requests.Count);

        client.ClearCache();
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "abc");
        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task Cache_expires()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h, cache: TimeSpan.FromMilliseconds(50));
        await client.GetEquipmentAsync(ArmoryRegion.Europe, 1307, "abc");
        await Task.Delay(120);
        await client.GetEquipmentAsync(ArmoryRegion.Europe, 1307, "abc");
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_call()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h);
        var tasks = Enumerable.Range(0, 5).Select(_ => client.GetEquipmentAsync(ArmoryRegion.Europe, 1307, "same")).ToArray();
        await Task.WhenAll(tasks);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Requests_are_rate_limited()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h, interval: TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "a");
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "b");
        await client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "c");
        sw.Stop();
        Assert.Equal(3, h.Requests.Count);
        Assert.True(sw.ElapsedMilliseconds >= 380, $"3 requests took only {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Default_options_allow_at_most_two_requests_per_second()
    {
        var o = new ArmoryClientOptions();
        Assert.True(o.MinRequestInterval >= TimeSpan.FromMilliseconds(500));
        Assert.InRange(o.CacheDuration, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Character_404_is_not_found_with_friendly_message()
    {
        var h = new FakeHandler().OnFixture("/api/character/info", "error_not_found.json", HttpStatusCode.NotFound, "application/problem+json");
        using var client = TestClients.Create(h);
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "AAAA"));
        Assert.Equal(ArmoryErrorKind.NotFound, ex.Kind);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("Character not found", ex.Message);
    }

    [Fact]
    public async Task Server_list_falls_back_to_page_when_api_moves()
    {
        var h = new FakeHandler()
            .On("/api/gameinfo/servers", "{\"status\":404}", HttpStatusCode.NotFound)
            .OnFixture("/characters/index", "characters_index_global.html", contentType: "text/html");
        using var client = TestClients.Create(h);
        var list = await client.GetServersAsync(ArmoryRegion.Europe);
        Assert.Equal(44, list.Count);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Korean_server_list_comes_from_page()
    {
        var h = new FakeHandler().OnFixture("/ko-kr/characters/index", "characters_index_kr.html", contentType: "text/html");
        using var client = TestClients.Create(h);
        var list = await client.GetServersAsync(ArmoryRegion.Korea);
        Assert.Equal(42, list.Count);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Html_instead_of_json_is_endpoint_changed()
    {
        var h = new FakeHandler().On("/api/character/equipment", "<html><body>Maintenance</body></html>", contentType: "text/html");
        using var client = TestClients.Create(h);
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.GetEquipmentAsync(ArmoryRegion.Europe, 1307, "x"));
        Assert.Equal(ArmoryErrorKind.EndpointChanged, ex.Kind);
    }

    [Fact]
    public async Task Network_failure_is_offline()
    {
        var h = new FakeHandler().OnThrow("/api/character/info",
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known", new SocketException(11001)));
        using var client = TestClients.Create(h);
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x"));
        Assert.Equal(ArmoryErrorKind.Offline, ex.Kind);
        Assert.Contains("internet", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ArmoryErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, ArmoryErrorKind.ServerError)]
    [InlineData(HttpStatusCode.TooManyRequests, ArmoryErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, ArmoryErrorKind.Blocked)]
    public async Task Http_status_mapping(HttpStatusCode status, ArmoryErrorKind kind)
    {
        var h = new FakeHandler().On("/api/character/info", "{}", status);
        using var client = TestClients.Create(h);
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x"));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var h = new FakeHandler().On("/api/character/info", "{}", HttpStatusCode.InternalServerError);
        using var client = TestClients.Create(h);
        await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x"));
        await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x"));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Timeout_is_reported()
    {
        var slow = new SlowHandler(TimeSpan.FromSeconds(5));
        using var client = new ArmoryClient(new HttpClient(slow), new ArmoryClientOptions
        {
            MinRequestInterval = TimeSpan.Zero,
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        });
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x"));
        Assert.Equal(ArmoryErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var slow = new SlowHandler(TimeSpan.FromSeconds(5));
        using var client = new ArmoryClient(new HttpClient(slow), new ArmoryClientOptions { MinRequestInterval = TimeSpan.Zero });
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCharacterAsync(ArmoryRegion.Europe, 1307, "x", cts.Token));
    }

    [Fact]
    public async Task Invalid_input_is_rejected_without_a_request()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h);
        var ex = await Assert.ThrowsAsync<ArmoryException>(() => client.SearchCharactersAsync(ArmoryRegion.Europe, "  "));
        Assert.Equal(ArmoryErrorKind.InvalidInput, ex.Kind);
        ex = await Assert.ThrowsAsync<ArmoryException>(() => client.SearchCharactersAsync(ArmoryRegion.Taiwan, "abc"));
        Assert.Equal(ArmoryErrorKind.InvalidInput, ex.Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task FindCharacter_returns_exact_match()
    {
        var h = FakeHandler.GlobalFixtures();
        using var client = TestClients.Create(h);
        var r = await client.FindCharacterAsync(ArmoryRegion.Europe, "testerx");
        Assert.NotNull(r);
        Assert.Equal("Testerx", r!.Name);
        Assert.Null(await client.FindCharacterAsync(ArmoryRegion.Europe, "nobody"));
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
