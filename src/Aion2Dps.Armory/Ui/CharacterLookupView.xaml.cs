using System.Windows;
using System.Windows.Controls;
using Aion2Dps.Armory.Ui;

namespace Aion2Dps.Armory;

/// <summary>
/// Character profile lookup (F14/F15): region + server + name search, results list, profile with gear (grade colours,
/// +enchant, exceed), item detail panel (main stats, substats, manastones, godstones), core/pantheon stats, titles,
/// daevanion boards, pet, wings and skills. Uses only theme resources from <c>ThemeKeys</c> via DynamicResource.
/// <para>Usage: <c>new CharacterLookupView(client)</c>, or XAML + <see cref="Initialize"/>.</para>
/// </summary>
public partial class CharacterLookupView : UserControl
{
    private bool _serversRequested;

    public CharacterLookupView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public CharacterLookupView(ArmoryClient client, bool loadIcons = true) : this()
    {
        Initialize(client, loadIcons);
    }

    /// <summary>The view model (null until <see cref="Initialize"/>).</summary>
    public CharacterLookupViewModel? ViewModel { get; private set; }

    /// <summary>Attaches a client. Safe to call once; later calls replace the view model.</summary>
    /// <param name="loadIcons">False disables icon downloads from the official asset CDN.</param>
    public void Initialize(ArmoryClient client, bool loadIcons = true)
    {
        ViewModel = new CharacterLookupViewModel(client, loadIcons);
        DataContext = ViewModel;
        _serversRequested = false;
        if (IsLoaded) RequestServers();
    }

    /// <summary>Looks up a player by exact name (e.g. from a meter row). Returns false when no exact match exists.</summary>
    public Task<bool> LookupAsync(ArmoryRegion region, string name, int? serverId = null) =>
        ViewModel?.LookupAsync(region, name, serverId) ?? Task.FromResult(false);

    private void OnLoaded(object sender, RoutedEventArgs e) => RequestServers();

    private void RequestServers()
    {
        if (_serversRequested || ViewModel is null) return;
        _serversRequested = true;
        _ = ViewModel.LoadServersAsync();
    }
}
