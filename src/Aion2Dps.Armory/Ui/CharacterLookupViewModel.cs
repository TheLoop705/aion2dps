using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Ui;

/// <summary>
/// State of <see cref="CharacterLookupView"/>. All members must be used from the UI thread (async continuations resume on
/// the caller's synchronization context).
/// </summary>
public sealed class CharacterLookupViewModel : ObservableObject
{
    private const string Area = "Armory";
    private readonly ArmoryClient _client;
    private readonly Func<string?, ImageSource?>? _images;

    private RegionOption _selectedRegion;
    private ServerOption? _selectedServer;
    private string _searchText = "";
    private bool _isLoadingServers;
    private bool _isSearching;
    private string? _searchError;
    private string _searchStatus = "";
    private CharacterSearchPage? _lastPage;
    private SearchResultVm? _selectedResult;
    private bool _isLoadingProfile;
    private string? _profileError;
    private ProfileViewModel? _profile;
    private ItemSlotVm? _selectedItem;
    private ItemDetailVm? _itemDetail;
    private bool _isLoadingItem;
    private string? _itemError;
    private string? _serverError;

    private CancellationTokenSource? _serversCts, _searchCts, _profileCts, _itemCts, _boardCts;
    private (ArmoryRegion Region, string Keyword, int? ServerId) _lastQuery;
    private IReadOnlyList<ArmoryServer> _serverCache = [];

    /// <param name="client">Client used for every lookup.</param>
    /// <param name="loadIcons">False disables item/profile icon downloads (offscreen renders, tests).</param>
    public CharacterLookupViewModel(ArmoryClient client, bool loadIcons = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _images = loadIcons ? ArmoryImages.Get : null;
        Regions = ArmoryRegionExtensions.All.Select(r => new RegionOption(r, r.DisplayName())).ToList();
        _selectedRegion = Regions[0];
        Servers.Add(AllServers);

        SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => !IsSearching && SearchText.Trim().Length > 0);
        LoadMoreCommand = new RelayCommand(_ => _ = LoadMoreAsync(), _ => !IsSearching && HasMoreResults);
        OpenItemCommand = new RelayCommand(p => { if (p is ItemSlotVm s) _ = OpenItemAsync(s); });
        CloseItemCommand = new RelayCommand(_ => CloseItem());
        SelectBoardCommand = new RelayCommand(p => { if (p is BoardVm b) _ = SelectBoardAsync(b); });
        RetryProfileCommand = new RelayCommand(_ => { if (_selectedResult is { } r) _ = OpenCharacterAsync(r.Result); });
        OpenOnSiteCommand = new RelayCommand(_ => OpenOnSite(), _ => Profile is not null);
    }

    private static readonly ServerOption AllServers = new(null, "All servers", null);

    public ArmoryClient Client => _client;
    public IReadOnlyList<RegionOption> Regions { get; }
    public ObservableCollection<ServerOption> Servers { get; } = [];
    public ObservableCollection<SearchResultVm> SearchResults { get; } = [];

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand OpenItemCommand { get; }
    public ICommand CloseItemCommand { get; }
    public ICommand SelectBoardCommand { get; }
    public ICommand RetryProfileCommand { get; }
    public ICommand OpenOnSiteCommand { get; }

    public RegionOption SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (value is null || !Set(ref _selectedRegion, value)) return;
            _ = LoadServersAsync();
        }
    }

    public ServerOption? SelectedServer { get => _selectedServer; set => Set(ref _selectedServer, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsLoadingServers { get => _isLoadingServers; private set => Set(ref _isLoadingServers, value); }
    public string? ServerError { get => _serverError; private set => Set(ref _serverError, value); }
    public bool IsSearching { get => _isSearching; private set { if (Set(ref _isSearching, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? SearchError { get => _searchError; private set => Set(ref _searchError, value); }
    public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }
    public bool HasMoreResults => _lastPage?.HasMore == true;

    public SearchResultVm? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!Set(ref _selectedResult, value) || value is null) return;
            _ = OpenCharacterAsync(value.Result);
        }
    }

    public bool IsLoadingProfile { get => _isLoadingProfile; private set => Set(ref _isLoadingProfile, value); }
    public string? ProfileError { get => _profileError; private set => Set(ref _profileError, value); }
    public ProfileViewModel? Profile
    {
        get => _profile;
        private set
        {
            if (Set(ref _profile, value))
            {
                OnPropertyChanged(nameof(ShowPlaceholder));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool ShowPlaceholder => _profile is null && !_isLoadingProfile && _profileError is null;

    public ItemSlotVm? SelectedItem { get => _selectedItem; private set { if (Set(ref _selectedItem, value)) OnPropertyChanged(nameof(IsItemPanelOpen)); } }
    public bool IsItemPanelOpen => _selectedItem is not null;
    public ItemDetailVm? ItemDetail { get => _itemDetail; private set => Set(ref _itemDetail, value); }
    public bool IsLoadingItem { get => _isLoadingItem; private set => Set(ref _isLoadingItem, value); }
    public string? ItemError { get => _itemError; private set => Set(ref _itemError, value); }

    // ───────────────────────────── operations ─────────────────────────────

    /// <summary>Loads the server dropdown of the selected region.</summary>
    public async Task LoadServersAsync()
    {
        var ct = Restart(ref _serversCts);
        var region = SelectedRegion.Region;
        IsLoadingServers = true;
        ServerError = null;
        Servers.Clear();
        Servers.Add(AllServers);
        SelectedServer = AllServers;
        try
        {
            var list = await _client.GetServersAsync(region, ct);
            if (ct.IsCancellationRequested) return;
            _serverCache = list;
            foreach (var s in list) Servers.Add(new ServerOption(s.ServerId, s.DisplayName, s));
        }
        catch (OperationCanceledException) { }
        catch (ArmoryException ex)
        {
            AppLog.Warn(Area, $"server list unavailable ({ex.Kind}): {ex.Message}");
            if (!ct.IsCancellationRequested)
                ServerError = ex.Kind == ArmoryErrorKind.Offline
                    ? ex.Message
                    : "Server list unavailable. Search still works across all servers.";
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, "server list failed", ex);
            if (!ct.IsCancellationRequested) ServerError = "Server list unavailable.";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingServers = false;
        }
    }

    /// <summary>Runs a new search with the current region / server / name.</summary>
    public async Task SearchAsync()
    {
        var keyword = SearchText.Trim();
        if (keyword.Length == 0)
        {
            SearchError = "Enter a character name to search.";
            return;
        }
        _lastQuery = (SelectedRegion.Region, keyword, SelectedServer?.ServerId);
        _lastPage = null;
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasMoreResults));
        await RunSearchPageAsync(1);
    }

    /// <summary>Appends the next page of the last search.</summary>
    public async Task LoadMoreAsync()
    {
        if (_lastPage is not { HasMore: true } p) return;
        await RunSearchPageAsync(p.Page + 1);
    }

    private async Task RunSearchPageAsync(int page)
    {
        var ct = Restart(ref _searchCts);
        var (region, keyword, serverId) = _lastQuery;
        IsSearching = true;
        SearchError = null;
        SearchStatus = page == 1 ? "Searching…" : "Loading more…";
        try
        {
            var result = await _client.SearchCharactersAsync(region, keyword, serverId, page, ct);
            if (ct.IsCancellationRequested) return;
            _lastPage = result;
            foreach (var r in result.Items) SearchResults.Add(new SearchResultVm(r, _images));
            SearchStatus = result.Total == 0 && SearchResults.Count == 0
                ? $"No characters named \"{keyword}\" found."
                : string.Create(CultureInfo.InvariantCulture, $"{SearchResults.Count:N0} of {result.Total:N0} shown");
        }
        catch (OperationCanceledException) { }
        catch (ArmoryException ex)
        {
            if (ct.IsCancellationRequested) return;
            SearchError = ex.Message;
            SearchStatus = "";
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, "search failed", ex);
            if (ct.IsCancellationRequested) return;
            SearchError = "Search failed unexpectedly.";
            SearchStatus = "";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
            OnPropertyChanged(nameof(HasMoreResults));
        }
    }

    /// <summary>Loads profile + equipment of a search result (or any known character).</summary>
    public async Task OpenCharacterAsync(CharacterSearchResult result)
    {
        var ct = Restart(ref _profileCts);
        Restart(ref _boardCts);
        CloseItem();
        IsLoadingProfile = true;
        ProfileError = null;
        Profile = null;
        OnPropertyChanged(nameof(ShowPlaceholder));
        var region = result.Region;
        try
        {
            var info = await _client.GetCharacterAsync(region, result.ServerId, result.CharacterId, ct);
            if (ct.IsCancellationRequested) return;
            var server = _serverCache.FirstOrDefault(s => s.ServerId == result.ServerId);
            var profile = new ProfileViewModel(info, server, _images) { IsLoadingEquipment = true };
            Profile = profile;
            _currentCharacter = (region, result.ServerId, result.CharacterId);
            IsLoadingProfile = false;
            OnPropertyChanged(nameof(ShowPlaceholder));

            var boardTask = profile.Boards.Count > 0 ? SelectBoardAsync(profile.Boards[0]) : Task.CompletedTask;
            try
            {
                var eq = await _client.GetEquipmentAsync(region, result.ServerId, result.CharacterId, ct);
                if (ct.IsCancellationRequested) return;
                profile.Equipment = eq;
            }
            catch (ArmoryException ex) when (!ct.IsCancellationRequested)
            {
                profile.EquipmentError = "Equipment unavailable: " + ex.Message;
            }
            finally
            {
                if (!ct.IsCancellationRequested) profile.IsLoadingEquipment = false;
            }
            await boardTask;
        }
        catch (OperationCanceledException) { }
        catch (ArmoryException ex)
        {
            if (ct.IsCancellationRequested) return;
            ProfileError = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, "profile failed", ex);
            if (ct.IsCancellationRequested) return;
            ProfileError = "Loading the character failed unexpectedly.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingProfile = false;
                OnPropertyChanged(nameof(ShowPlaceholder));
            }
        }
    }

    /// <summary>Searches <paramref name="name"/> and opens the exact match (for "look up this player" from the meter).
    /// Returns false when no exact match was found (the results list then shows the partial matches).</summary>
    public async Task<bool> LookupAsync(ArmoryRegion region, string name, int? serverId = null)
    {
        var opt = Regions.FirstOrDefault(r => r.Region == region) ?? Regions[0];
        if (opt != _selectedRegion)
        {
            _selectedRegion = opt;
            OnPropertyChanged(nameof(SelectedRegion));
            await LoadServersAsync();
        }
        SelectedServer = Servers.FirstOrDefault(s => s.ServerId == serverId) ?? AllServers;
        SearchText = name;
        await SearchAsync();
        var match = SearchResults.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)
                                                      && (serverId is null || r.Result.ServerId == serverId));
        if (match is null) return false;
        _selectedResult = match;
        OnPropertyChanged(nameof(SelectedResult));
        await OpenCharacterAsync(match.Result);
        return true;
    }

    private (ArmoryRegion Region, int ServerId, string CharacterId)? _currentCharacter;

    public async Task OpenItemAsync(ItemSlotVm slot)
    {
        if (_currentCharacter is not { } c) return;
        var ct = Restart(ref _itemCts);
        SelectedItem = slot;
        ItemDetail = null;
        ItemError = null;
        IsLoadingItem = true;
        try
        {
            var d = await _client.GetItemDetailAsync(c.Region, c.ServerId, c.CharacterId, slot.Item, ct);
            if (ct.IsCancellationRequested) return;
            ItemDetail = new ItemDetailVm(d, slot.Item, _images);
        }
        catch (OperationCanceledException) { }
        catch (ArmoryException ex)
        {
            if (!ct.IsCancellationRequested) ItemError = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, "item detail failed", ex);
            if (!ct.IsCancellationRequested) ItemError = "Loading the item failed unexpectedly.";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingItem = false;
        }
    }

    public void CloseItem()
    {
        _itemCts?.Cancel();
        SelectedItem = null;
        ItemDetail = null;
        ItemError = null;
        IsLoadingItem = false;
    }

    public async Task SelectBoardAsync(BoardVm board)
    {
        var profile = Profile;
        if (profile is null || _currentCharacter is not { } c) return;
        var ct = Restart(ref _boardCts);
        foreach (var b in profile.Boards) b.IsSelected = ReferenceEquals(b, board);
        profile.BoardError = null;
        profile.IsLoadingBoard = true;
        profile.SetBoard(null);
        try
        {
            var d = await _client.GetDaevanionAsync(c.Region, c.ServerId, c.CharacterId, board.Board.Id, ct);
            if (ct.IsCancellationRequested) return;
            profile.SetBoard(d);
        }
        catch (OperationCanceledException) { }
        catch (ArmoryException ex)
        {
            if (!ct.IsCancellationRequested) profile.BoardError = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error(Area, "daevanion failed", ex);
            if (!ct.IsCancellationRequested) profile.BoardError = "Loading the board failed unexpectedly.";
        }
        finally
        {
            if (!ct.IsCancellationRequested) profile.IsLoadingBoard = false;
        }
    }

    private void OpenOnSite()
    {
        if (_currentCharacter is not { } c) return;
        try
        {
            var url = _client.Endpoints.CharacterPage(c.Region, c.ServerId, c.CharacterId);
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(Area, $"could not open browser: {ex.Message}");
        }
    }

    private static CancellationToken Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }
}
