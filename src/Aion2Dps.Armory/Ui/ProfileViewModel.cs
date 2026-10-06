using System.Collections.ObjectModel;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Ui;

/// <summary>Display model of one looked-up character (info + equipment).</summary>
public sealed class ProfileViewModel : ObservableObject
{
    private IReadOnlyList<NodeVm> _boardNodes = [];
    private IReadOnlyList<string> _boardStatEffects = [];
    private IReadOnlyList<string> _boardSkillEffects = [];
    private bool _isLoadingBoard;
    private string? _boardError;
    private double _boardWidth;
    private double _boardHeight;
    private CharacterEquipment? _equipment;
    private string? _equipmentError;
    private bool _isLoadingEquipment;

    public ProfileViewModel(CharacterInfo info, ArmoryServer? server, Func<string?, ImageSource?>? images)
    {
        Info = info;
        Images = images;
        var p = info.Profile;
        Name = p.Name;
        Class = p.Class;
        ClassText = p.Class != CharacterClass.Unknown ? ArmorySlots.ClassDisplayName(p.Class) : p.ClassName ?? "Unknown class";
        var parts = new List<string> { ClassText, $"Lv {p.Level}" };
        var serverName = p.ServerName ?? server?.Name ?? $"Server {p.ServerId}";
        parts.Add(serverName);
        var faction = p.Faction != Faction.Unknown ? p.Faction.ToString() : p.RaceName;
        if (faction is not null) parts.Add(faction);
        Subtitle = string.Join(" · ", parts);
        RegionText = info.Region.DisplayName();
        TitleText = p.TitleName is { } t ? $"Title: {t}" : "";
        TitleBrush = GradeBrushes.For(ArmorySlots.ParseGrade(p.TitleGrade));
        LegionText = p.LegionName is { } l ? $"Legion: {l}" : "";
        CombatPowerText = ArmoryText.Abbreviate(p.CombatPower);
        CombatPowerExact = p.CombatPower is null ? "Combat Power not provided" : $"Combat Power {ArmoryText.Exact(p.CombatPower)}";
        ItemLevelText = p.ItemLevel is { } il ? ArmoryText.Exact(il) : "—";
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
        ProfileImage = images?.Invoke(p.ProfileImageUrl);

        CoreStats = info.Stats.Where(s => s.Category == StatCategory.Core).Select(s => new StatVm(s)).ToList();
        PantheonStats = info.Stats.Where(s => s.Category == StatCategory.Pantheon).Select(s => new StatVm(s)).ToList();
        OtherStats = info.Stats.Where(s => s.Category == StatCategory.Other).Select(s => new StatVm(s)).ToList();
        Titles = info.Titles.Titles.Select(t => new TitleVm(t)).ToList();
        TitlesHeader = info.Titles.TotalCount > 0
            ? $"Titles ({info.Titles.OwnedCount} of {info.Titles.TotalCount} owned)"
            : $"Titles ({info.Titles.Titles.Count})";
        Boards = new ObservableCollection<BoardVm>(info.DaevanionBoards.Select(b => new BoardVm(b)));
        Rankings = info.Rankings.Where(r => r.ContentName is not null)
            .Select(r => $"{r.ContentName}: #{r.Rank?.ToString() ?? "—"}{(r.GradeName is { } g ? $" ({g})" : "")}").ToList();
    }

    public CharacterInfo Info { get; }
    internal Func<string?, ImageSource?>? Images { get; }
    public string Name { get; }
    public CharacterClass Class { get; }
    public string ClassText { get; }
    public string Subtitle { get; }
    public string RegionText { get; }
    public string TitleText { get; }
    public Brush TitleBrush { get; }
    public string LegionText { get; }
    public string CombatPowerText { get; }
    public string CombatPowerExact { get; }
    public string ItemLevelText { get; }
    public string Initial { get; }
    public ImageSource? ProfileImage { get; }
    public IReadOnlyList<StatVm> CoreStats { get; }
    public IReadOnlyList<StatVm> PantheonStats { get; }
    public IReadOnlyList<StatVm> OtherStats { get; }
    public IReadOnlyList<TitleVm> Titles { get; }
    public string TitlesHeader { get; }
    public ObservableCollection<BoardVm> Boards { get; }
    public IReadOnlyList<string> Rankings { get; }

    // ── equipment (loaded separately) ──

    public CharacterEquipment? Equipment
    {
        get => _equipment;
        set
        {
            if (!Set(ref _equipment, value)) return;
            var items = value?.Items ?? [];
            WeaponsAndArmor = items.Where(i => i.Group is SlotGroup.Weapon or SlotGroup.Armor).Select(i => new ItemSlotVm(i, Images)).ToList();
            Accessories = items.Where(i => i.Group is SlotGroup.Accessory or SlotGroup.Other).Select(i => new ItemSlotVm(i, Images)).ToList();
            ArcanaAndRunes = items.Where(i => i.Group is SlotGroup.Arcana or SlotGroup.Rune).Select(i => new ItemSlotVm(i, Images)).ToList();
            var companions = new List<CompanionVm>();
            if (value?.Pet is { } pet)
                companions.Add(new CompanionVm { Kind = "Pet", Name = pet.Name ?? "—", Detail = pet.Level is { } lv ? $"Lv {lv}" : "", Icon = Images?.Invoke(pet.Icon) });
            if (value?.Wing is { } w)
                companions.Add(new CompanionVm { Kind = "Wings", Name = w.Name ?? "—", Detail = Detail(w), GradeBrush = GradeBrushes.For(w.Grade), Icon = Images?.Invoke(w.Icon) });
            if (value?.WingSkin is { } ws)
                companions.Add(new CompanionVm { Kind = "Wing appearance", Name = ws.Name ?? "—", Detail = Detail(ws), GradeBrush = GradeBrushes.For(ws.Grade), Icon = Images?.Invoke(ws.Icon) });
            Companions = companions;
            var skills = value?.Skills ?? [];
            SkillGroups = skills.Where(s => s.Acquired || s.Level > 0)
                .GroupBy(s => s.Category ?? "Other")
                .OrderBy(g => g.Key switch { "Active" => 0, "Passive" => 1, "Stigma" => 2, "Dp" => 3, _ => 4 })
                .Select(g => new SkillGroupVm(g.Key == "Dp" ? "DP" : g.Key,g.OrderByDescending(s => s.Equipped).ThenBy(s => s.NeedLevel).Select(s => new SkillVm(s, Images)).ToList()))
                .ToList();
            OnPropertyChanged(nameof(WeaponsAndArmor));
            OnPropertyChanged(nameof(Accessories));
            OnPropertyChanged(nameof(ArcanaAndRunes));
            OnPropertyChanged(nameof(Companions));
            OnPropertyChanged(nameof(SkillGroups));
            OnPropertyChanged(nameof(HasEquipment));
        }
    }

    private static string Detail(WingInfo w)
    {
        var s = GradeBrushes.DisplayName(w.Grade, w.GradeName);
        return w.EnchantLevel is > 0 ? $"{s} · +{w.EnchantLevel}" : s;
    }

    public bool HasEquipment => _equipment is not null;
    public IReadOnlyList<ItemSlotVm> WeaponsAndArmor { get; private set; } = [];
    public IReadOnlyList<ItemSlotVm> Accessories { get; private set; } = [];
    public IReadOnlyList<ItemSlotVm> ArcanaAndRunes { get; private set; } = [];
    public IReadOnlyList<CompanionVm> Companions { get; private set; } = [];
    public IReadOnlyList<SkillGroupVm> SkillGroups { get; private set; } = [];

    public bool IsLoadingEquipment { get => _isLoadingEquipment; set => Set(ref _isLoadingEquipment, value); }
    public string? EquipmentError { get => _equipmentError; set => Set(ref _equipmentError, value); }

    // ── selected daevanion board ──

    public bool IsLoadingBoard { get => _isLoadingBoard; set => Set(ref _isLoadingBoard, value); }
    public string? BoardError { get => _boardError; set => Set(ref _boardError, value); }
    public IReadOnlyList<NodeVm> BoardNodes { get => _boardNodes; private set => Set(ref _boardNodes, value); }
    public IReadOnlyList<string> BoardStatEffects { get => _boardStatEffects; private set => Set(ref _boardStatEffects, value); }
    public IReadOnlyList<string> BoardSkillEffects { get => _boardSkillEffects; private set => Set(ref _boardSkillEffects, value); }
    public double BoardWidth { get => _boardWidth; private set => Set(ref _boardWidth, value); }
    public double BoardHeight { get => _boardHeight; private set => Set(ref _boardHeight, value); }

    public void SetBoard(DaevanionBoardDetail? detail)
    {
        if (detail is null)
        {
            BoardNodes = [];
            BoardStatEffects = [];
            BoardSkillEffects = [];
            BoardWidth = BoardHeight = 0;
            return;
        }
        var visible = detail.Nodes.Where(n => n.Type != DaevanionNodeType.None).ToList();
        if (visible.Count == 0)
        {
            BoardNodes = [];
        }
        else
        {
            var minRow = visible.Min(n => n.Row);
            var minCol = visible.Min(n => n.Col);
            BoardNodes = visible.Select(n => new NodeVm { Node = n, X = (n.Col - minCol) * NodeVm.Cell, Y = (n.Row - minRow) * NodeVm.Cell }).ToList();
            BoardWidth = (visible.Max(n => n.Col) - minCol + 1) * NodeVm.Cell;
            BoardHeight = (visible.Max(n => n.Row) - minRow + 1) * NodeVm.Cell;
        }
        BoardStatEffects = detail.OpenStatEffects;
        BoardSkillEffects = detail.OpenSkillEffects;
    }
}
