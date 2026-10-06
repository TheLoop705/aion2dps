using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory;

public sealed class ArmoryClientOptions
{
    /// <summary>How long successful responses are reused (default 10 minutes).</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Minimum spacing between two requests to the official site (default 500 ms = at most 2 requests/s).</summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Per-request timeout (default 15 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Language for the Global regions: en-US (default), de-DE, fr-FR, es-ES, ja-JP, pt-BR.</summary>
    public string GlobalLanguage { get; set; } = "en-US";
    /// <summary>User-Agent sent with every request.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Aion2Dps/0.1";
    /// <summary>Clock used by the cache and the rate limiter (tests can inject one).</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Read-only client for the official AION 2 character-info endpoints (the ones NCSOFT's own "Character Info" pages call).
/// Unauthenticated GETs only. Responses are cached in memory, requests are spaced (≤ 2/s by default) and failures are
/// reported as <see cref="ArmoryException"/> with a user-friendly message. Thread-safe.
/// </summary>
public sealed class ArmoryClient : IDisposable
{
    private const string Area = "Armory";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ArmoryClientOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflight = new();
    private long _requestCount;

    private sealed record CacheEntry(DateTimeOffset Expires, object Value);

    public ArmoryClient(HttpClient? http = null) : this(http, null) { }

    public ArmoryClient(HttpClient? http, ArmoryClientOptions? options)
    {
        _options = options ?? new ArmoryClientOptions();
        _time = _options.TimeProvider ?? TimeProvider.System;
        Endpoints = new ArmoryEndpoints(_options.GlobalLanguage);
        if (http is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public ArmoryEndpoints Endpoints { get; }

    public ArmoryClientOptions Options => _options;

    /// <summary>Number of HTTP requests actually sent (cache hits excluded). For diagnostics and tests.</summary>
    public long RequestCount => Interlocked.Read(ref _requestCount);

    public void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }

    // ───────────────────────────── public API ─────────────────────────────

    /// <summary>Server list of a region (both factions), sorted Elyos first then by id. Global uses the
    /// <c>gameinfo/servers</c> API with the characters page as fallback; KR/TW read the page's embedded server map.</summary>
    public async Task<IReadOnlyList<ArmoryServer>> GetServersAsync(ArmoryRegion region, CancellationToken cancellationToken = default)
    {
        if (Endpoints.Servers(region) is { } api)
        {
            try
            {
                var list = await GetAsync(api, region, LookupKind.List, json => ArmoryParser.ParseServers(json, region), cancellationToken)
                    .ConfigureAwait(false);
                if (list.Count > 0) return list;
            }
            catch (ArmoryException ex) when (ex.Kind is ArmoryErrorKind.EndpointChanged or ArmoryErrorKind.NotFound or ArmoryErrorKind.Blocked)
            {
                AppLog.Warn(Area, $"server list API failed ({ex.Kind}), falling back to the characters page");
            }
        }
        return await GetAsync(Endpoints.CharactersPage(region), region, LookupKind.List,
            html => ArmoryParser.ParseServerMapFromHtml(html, region), cancellationToken, acceptHtml: true).ConfigureAwait(false);
    }

    /// <summary>Class list (Global only; empty for KR/TW).</summary>
    public async Task<IReadOnlyList<ArmoryClassInfo>> GetClassesAsync(ArmoryRegion region, CancellationToken cancellationToken = default)
    {
        if (Endpoints.Classes(region) is not { } url) return [];
        return await GetAsync(url, region, LookupKind.List, ArmoryParser.ParseClasses, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>pcId → class/gender/race table (Global only; empty for KR/TW, where <see cref="ArmorySlots.ClassFromPcId"/> applies).</summary>
    public async Task<IReadOnlyList<ArmoryPcData>> GetPcDataAsync(ArmoryRegion region, CancellationToken cancellationToken = default)
    {
        if (Endpoints.PcData(region) is not { } url) return [];
        return await GetAsync(url, region, LookupKind.List, ArmoryParser.ParsePcData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Searches characters by (partial) name. <paramref name="page"/> starts at 1. Taiwan requires a server.</summary>
    public async Task<CharacterSearchPage> SearchCharactersAsync(ArmoryRegion region, string name, int? serverId = null, int page = 1,
        CancellationToken cancellationToken = default, int pageSize = 40)
    {
        var keyword = (name ?? "").Trim();
        if (keyword.Length == 0)
            throw new ArmoryException(ArmoryErrorKind.InvalidInput, "Enter a character name to search.");
        if (region == ArmoryRegion.Taiwan && serverId is null)
            throw new ArmoryException(ArmoryErrorKind.InvalidInput, "Taiwan search needs a server. Pick one and search again.");
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var url = Endpoints.Search(region, keyword, serverId, page, pageSize);
        return await GetAsync(url, region, LookupKind.Search, json => ArmoryParser.ParseSearch(json, region), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>First search result whose name equals <paramref name="name"/> (case-insensitive), or null.</summary>
    public async Task<CharacterSearchResult?> FindCharacterAsync(ArmoryRegion region, string name, int? serverId = null,
        CancellationToken cancellationToken = default)
    {
        var page = await SearchCharactersAsync(region, name, serverId, 1, cancellationToken).ConfigureAwait(false);
        var trimmed = name.Trim();
        return page.Items.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase)
                                              && (serverId is null || r.ServerId == serverId));
    }

    /// <summary>Profile, core/pantheon stats, titles, rankings and daevanion board summaries.</summary>
    public Task<CharacterInfo> GetCharacterAsync(ArmoryRegion region, int serverId, string characterId, CancellationToken cancellationToken = default)
    {
        Require(characterId);
        return GetAsync(Endpoints.CharacterInfo(region, serverId, characterId), region, LookupKind.Character,
            json => ArmoryParser.ParseCharacterInfo(json, region), cancellationToken);
    }

    /// <summary>Equipped gear (incl. arcana and runes), appearance skins, pet, wings and skills.</summary>
    public Task<CharacterEquipment> GetEquipmentAsync(ArmoryRegion region, int serverId, string characterId, CancellationToken cancellationToken = default)
    {
        Require(characterId);
        return GetAsync(Endpoints.Equipment(region, serverId, characterId), region, LookupKind.Character,
            ArmoryParser.ParseEquipment, cancellationToken);
    }

    /// <summary>Rolled stats of an equipped item: main stats, substats, manastones, godstones, set bonuses.</summary>
    public Task<ItemDetail> GetItemDetailAsync(ArmoryRegion region, int serverId, string characterId, long itemId, int enchantLevel, int slotPos,
        CancellationToken cancellationToken = default)
    {
        Require(characterId);
        return GetAsync(Endpoints.ItemDetail(region, serverId, characterId, itemId, enchantLevel, slotPos), region, LookupKind.Item,
            ArmoryParser.ParseItemDetail, cancellationToken);
    }

    /// <summary>Convenience overload taking an item from <see cref="GetEquipmentAsync"/>.</summary>
    public Task<ItemDetail> GetItemDetailAsync(ArmoryRegion region, int serverId, string characterId, EquipmentItem item,
        CancellationToken cancellationToken = default) =>
        GetItemDetailAsync(region, serverId, characterId, item.Id, item.EnchantLevel, item.SlotPos, cancellationToken);

    /// <summary>Nodes and opened effects of one daevanion board (board ids come from <see cref="CharacterInfo.DaevanionBoards"/>).</summary>
    public Task<DaevanionBoardDetail> GetDaevanionAsync(ArmoryRegion region, int serverId, string characterId, int boardId,
        CancellationToken cancellationToken = default)
    {
        Require(characterId);
        return GetAsync(Endpoints.Daevanion(region, serverId, characterId, boardId), region, LookupKind.Board,
            json => ArmoryParser.ParseDaevanion(json, boardId), cancellationToken);
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    private enum LookupKind { List, Search, Character, Item, Board }

    private static void Require(string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
            throw new ArmoryException(ArmoryErrorKind.InvalidInput, "No character selected.");
    }

    private async Task<T> GetAsync<T>(Uri url, ArmoryRegion region, LookupKind kind, Func<string, T> parse, CancellationToken ct,
        bool acceptHtml = false) where T : notnull
    {
        var key = url.AbsoluteUri;
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit))
        {
            if (hit.Expires > now && hit.Value is T cached) return cached;
            _cache.TryRemove(key, out _);
        }

        // Share one in-flight request between concurrent callers of the same URL.
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<object>>(async () =>
        {
            try
            {
                var body = await SendAsync(url, region, kind, acceptHtml, CancellationToken.None).ConfigureAwait(false);
                object value = parse(body);
                _cache[key] = new CacheEntry(_time.GetUtcNow() + _options.CacheDuration, value);
                return value;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        var result = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        return (T)result;
    }

    private async Task<string> SendAsync(Uri url, ArmoryRegion region, LookupKind kind, bool acceptHtml, CancellationToken ct)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _requestCount);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptHtml ? "text/html" : "application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        req.Headers.Referrer = Endpoints.CharactersPage(region);
        req.Headers.AcceptLanguage.TryParseAdd(region switch
        {
            ArmoryRegion.Korea => "ko-KR,ko;q=0.9",
            ArmoryRegion.Taiwan => "zh-TW,zh;q=0.9",
            _ => Endpoints.GlobalLanguage + ",en;q=0.8",
        });

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn(Area, $"timeout: {url}");
            throw new ArmoryException(ArmoryErrorKind.Timeout, ArmoryException.DefaultMessage(ArmoryErrorKind.Timeout), url: url.AbsoluteUri);
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warn(Area, $"request failed: {url}: {ex.Message}");
            var kindErr = ex.StatusCode is null || ex.InnerException is SocketException || ex.HttpRequestError is
                HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
                ? ArmoryErrorKind.Offline : ArmoryErrorKind.ServerError;
            throw new ArmoryException(kindErr, ArmoryException.DefaultMessage(kindErr), ex, ex.StatusCode, url.AbsoluteUri);
        }

        using (resp)
        {
            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ArmoryException(ArmoryErrorKind.Timeout, ArmoryException.DefaultMessage(ArmoryErrorKind.Timeout), url: url.AbsoluteUri);
            }
            catch (HttpRequestException ex)
            {
                throw new ArmoryException(ArmoryErrorKind.Offline, ArmoryException.DefaultMessage(ArmoryErrorKind.Offline), ex, null, url.AbsoluteUri);
            }

            if (resp.IsSuccessStatusCode)
            {
                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!acceptHtml && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    throw new ArmoryException(ArmoryErrorKind.EndpointChanged,
                        "The official site returned a web page instead of data. The character site may have changed.",
                        statusCode: resp.StatusCode, url: url.AbsoluteUri);
                return body;
            }

            AppLog.Warn(Area, $"HTTP {(int)resp.StatusCode} for {url}");
            throw MapStatus(resp.StatusCode, kind, region, url);
        }
    }

    private static ArmoryException MapStatus(HttpStatusCode status, LookupKind kind, ArmoryRegion region, Uri url)
    {
        var code = (int)status;
        var (k, msg) = code switch
        {
            404 when kind == LookupKind.Character => (ArmoryErrorKind.NotFound,
                "Character not found. It may have been renamed, moved or deleted, or the region is wrong."),
            404 when kind == LookupKind.Item => (ArmoryErrorKind.NotFound, "Item details are not available for this slot."),
            404 when kind == LookupKind.Board => (ArmoryErrorKind.NotFound, "This daevanion board is not available."),
            404 => (ArmoryErrorKind.EndpointChanged, ArmoryException.DefaultMessage(ArmoryErrorKind.EndpointChanged)),
            400 when kind == LookupKind.Search && region == ArmoryRegion.Taiwan => (ArmoryErrorKind.InvalidInput,
                "The Taiwan site rejected the search. Pick a server and try a longer name."),
            400 when kind == LookupKind.Search => (ArmoryErrorKind.InvalidInput, "The official site rejected this search. Try a longer name."),
            400 => (ArmoryErrorKind.EndpointChanged, ArmoryException.DefaultMessage(ArmoryErrorKind.EndpointChanged)),
            401 or 403 => (ArmoryErrorKind.Blocked, ArmoryException.DefaultMessage(ArmoryErrorKind.Blocked)),
            408 => (ArmoryErrorKind.Timeout, ArmoryException.DefaultMessage(ArmoryErrorKind.Timeout)),
            429 => (ArmoryErrorKind.RateLimited, ArmoryException.DefaultMessage(ArmoryErrorKind.RateLimited)),
            >= 500 => (ArmoryErrorKind.ServerError, ArmoryException.DefaultMessage(ArmoryErrorKind.ServerError)),
            _ => (ArmoryErrorKind.EndpointChanged, $"The official site answered HTTP {code}. The character site may have changed."),
        };
        return new ArmoryException(k, msg, statusCode: status, url: url.AbsoluteUri);
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var interval = _options.MinRequestInterval;
            if (interval > TimeSpan.Zero && _lastRequest != DateTimeOffset.MinValue)
            {
                var wait = _lastRequest + interval - _time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
            _lastRequest = _time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }
}
