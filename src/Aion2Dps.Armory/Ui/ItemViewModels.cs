using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Ui;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed record RegionOption(ArmoryRegion Region, string Name)
{
    public override string ToString() => Name;
}

public sealed record ServerOption(int? ServerId, string Name, ArmoryServer? Server)
{
    public override string ToString() => Name;
}

public sealed class SearchResultVm
{
    public SearchResultVm(CharacterSearchResult r, Func<string?, ImageSource?>? images)
    {
        Result = r;
        Image = images?.Invoke(r.ProfileImageUrl);
    }

    public CharacterSearchResult Result { get; }
    public string Name => Result.Name;
    public CharacterClass Class => Result.Class;
    public string ClassLevel => $"{ArmorySlots.ClassDisplayName(Result.Class)} · Lv {Result.Level}";
    public string ServerText => Result.Faction == Faction.Unknown
        ? Result.ServerName ?? $"Server {Result.ServerId}"
        : $"{Result.ServerName ?? $"Server {Result.ServerId}"} · {Result.Faction}";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public ImageSource? Image { get; }
}

public sealed class ItemSlotVm
{
    public ItemSlotVm(EquipmentItem item, Func<string?, ImageSource?>? images)
    {
        Item = item;
        Icon = images?.Invoke(item.Icon);
    }

    public EquipmentItem Item { get; }
    public string SlotLabel => ArmorySlots.DisplaySlotName(Item.SlotName, Item.SlotPos);
    public string Name => Item.Name.Length == 0 ? "—" : Item.Name;
    public string EnchantText => Item.EnchantLevel > 0 ? $"+{Item.EnchantLevel}" : "";
    public string ExceedText => Item.ExceedLevel > 0 ? $"Exceed {Item.ExceedLevel}" : "";
    public Brush GradeBrush => GradeBrushes.For(Item.Grade);
    public string GradeText => GradeBrushes.DisplayName(Item.Grade, Item.GradeName);
    public ImageSource? Icon { get; }
    public string Initial => SlotLabel.Length > 0 ? SlotLabel[..1] : "?";
    public string ToolTipText
    {
        get
        {
            var s = $"{Name}{(Item.EnchantLevel > 0 ? $" +{Item.EnchantLevel}" : "")}\n{GradeText} · {SlotLabel}";
            if (Item.ExceedLevel > 0) s += $"\nExceed {Item.ExceedLevel}";
            return s + "\nClick for stats, substats and sockets";
        }
    }
}

public sealed class StatVm
{
    public StatVm(CharacterStat s)
    {
        Stat = s;
    }

    public CharacterStat Stat { get; }
    public string Name => Stat.Name;
    public string Value => Stat.Value.ToString("N0", CultureInfo.InvariantCulture);
    public string EffectsText => Stat.Effects.Count == 0 ? "" : string.Join("\n", Stat.Effects);
    public string ToolTipText => Stat.Effects.Count == 0 ? $"{Name}: {Value}" : $"{Name}: {Value}\n{EffectsText}";
}

public sealed class TitleVm
{
    public TitleVm(CharacterTitle t)
    {
        Title = t;
    }

    public CharacterTitle Title { get; }
    public string Name => Title.Name;
    public string Category => Title.Category ?? "";
    public Brush GradeBrush => GradeBrushes.For(ArmorySlots.ParseGrade(Title.Grade));
    public string EquipText => Title.EquipStats.Count == 0 ? "" : "Equipped: " + string.Join(", ", Title.EquipStats);
    public string OwnedText => Title.Stats.Count == 0 ? "" : "Collection: " + string.Join(", ", Title.Stats);
    public string CountText => Title.TotalCount > 0 ? $"{Title.OwnedCount}/{Title.TotalCount}" : "";
}

public sealed class SkillVm
{
    public SkillVm(SkillInfo s, Func<string?, ImageSource?>? images)
    {
        Skill = s;
        Icon = images?.Invoke(s.Icon);
    }

    public SkillInfo Skill { get; }
    public string Name => Skill.Name;
    public string LevelText => Skill.Level > 0 ? $"Lv {Skill.Level}" : "—";
    public ImageSource? Icon { get; }
}

public sealed class SkillGroupVm(string title, IReadOnlyList<SkillVm> skills)
{
    public string Title { get; } = title;
    public IReadOnlyList<SkillVm> Skills { get; } = skills;
    public string Header => $"{Title} ({Skills.Count})";
}

public sealed class CompanionVm
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public Brush? GradeBrush { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed class BoardVm : ObservableObject
{
    private bool _isSelected;

    public BoardVm(DaevanionBoard b)
    {
        Board = b;
    }

    public DaevanionBoard Board { get; }
    public string Name => Board.Name;
    public string Progress => $"{Board.OpenNodes}/{Board.TotalNodes}";
    public double Percent => Board.Percent;
    public string PercentText => Board.TotalNodes > 0 ? $"{Board.Percent:0}%" : "—";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class NodeVm
{
    public const double Cell = 13;

    public required DaevanionNode Node { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Size => Cell - 2;
    /// <summary>"Start", "OpenStat", "OpenSkill", "ClosedStat", "ClosedSkill".</summary>
    public string State => Node.Type == DaevanionNodeType.Start ? "Start"
        : (Node.Open ? "Open" : "Closed") + (Node.Type == DaevanionNodeType.SkillLevel ? "Skill" : "Stat");
    public string ToolTipText
    {
        get
        {
            var name = Node.Name.Length > 0 ? Node.Name : Node.Type.ToString();
            var eff = Node.Effects.Count > 0 ? "\n" + string.Join("\n", Node.Effects) : "";
            return $"{name}{(Node.Open ? " (open)" : "")}{eff}";
        }
    }
}

public sealed class ItemStatLineVm
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public string Extra { get; init; } = "";
    public Brush? Brush { get; init; }
}

public sealed class GodstoneVm
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Brush Brush { get; init; }
}

/// <summary>Display model of <see cref="ItemDetail"/> for the detail panel.</summary>
public sealed class ItemDetailVm
{
    public ItemDetailVm(ItemDetail d, EquipmentItem? slot, Func<string?, ImageSource?>? images)
    {
        Detail = d;
        Icon = images?.Invoke(d.Icon ?? slot?.Icon);
        var exceed = slot?.ExceedLevel ?? 0;
        Name = d.Name.Length > 0 ? d.Name : slot?.Name ?? "—";
        GradeBrush = GradeBrushes.For(d.Grade != ItemGrade.Unknown ? d.Grade : slot?.Grade ?? ItemGrade.Unknown);
        var parts = new List<string>();
        if (d.CategoryName is { } c) parts.Add(c);
        parts.Add(d.GradeName ?? GradeBrushes.DisplayName(d.Grade, null));
        if (d.Level is > 0) parts.Add($"Item Lv {d.Level}");
        Subtitle = string.Join(" · ", parts);
        var enchant = d.EnchantLevel > 0 ? d.EnchantLevel : slot?.EnchantLevel ?? 0;
        EnchantText = d.MaxEnchantLevel is > 0 ? $"+{enchant} / {d.MaxEnchantLevel}" : $"+{enchant}";
        ExceedText = exceed > 0 ? (d.MaxExceedLevel is > 0 ? $"Exceed {exceed} / {d.MaxExceedLevel}" : $"Exceed {exceed}") : "";
        var req = new List<string>();
        if (d.EquipLevel is > 0) req.Add($"Requires Lv {d.EquipLevel}");
        if (d.ClassNames.Count > 0) req.Add(string.Join(", ", d.ClassNames));
        if (d.RaceName is { } r) req.Add(r);
        Requirements = string.Join(" · ", req);
        MainStats = d.MainStats.Select(s => new ItemStatLineVm
        {
            Name = s.Name,
            Value = s.MinValue is { } min && min != s.Value ? $"{min} - {s.Value}" : s.Value,
            Extra = s.Extra is { } e && e != "0" ? $"(+{e})" : "",
        }).ToList();
        var subs = d.SubStats.Select(s => new ItemStatLineVm { Name = s.Name, Value = s.Value }).ToList();
        subs.AddRange(d.SubSkills.Select(s => new ItemStatLineVm { Name = s.Name, Value = $"+{s.Level}" }));
        SubStats = subs;
        SubStatsHeader = d.SoulBindRate is { } sb ? $"Substats (soul bind {sb}%)" : "Substats";
        Manastones = d.Manastones.OrderBy(m => m.SlotPos).Select(m => new ItemStatLineVm
        {
            Name = m.Name,
            Value = m.Value,
            Brush = GradeBrushes.For(m.Grade),
        }).ToList();
        var emptySockets = Math.Max(0, d.MagicStoneSlotCount - d.Manastones.Count);
        ManastoneHeader = d.MagicStoneSlotCount > 0
            ? $"Manastones ({d.Manastones.Count}/{d.MagicStoneSlotCount}{(emptySockets > 0 ? $", {emptySockets} empty" : "")})"
            : "Manastones";
        Godstones = d.Godstones.Select(g => new GodstoneVm { Name = g.Name, Description = g.Description, Brush = GradeBrushes.For(g.Grade) }).ToList();
        GodstoneHeader = d.GodStoneSlotCount > 0 ? $"Godstone ({d.Godstones.Count}/{d.GodStoneSlotCount})" : "Godstone";
        HasSockets = d.MagicStoneSlotCount > 0 || d.Manastones.Count > 0;
        HasGodstoneSlot = d.GodStoneSlotCount > 0 || d.Godstones.Count > 0;
        SetText = d.SetBonuses.Count == 0 ? "" : string.Join("\n", d.SetBonuses.Select(b =>
            (b.Degree is { } deg ? $"{deg} pc: " : "") + string.Join(", ", b.Descriptions)));
        Costumes = d.Costumes.Count == 0 ? "" : string.Join(", ", d.Costumes);
        Sources = d.Sources.Count == 0 ? "" : string.Join(", ", d.Sources);
    }

    public ItemDetail Detail { get; }
    public string Name { get; }
    public Brush GradeBrush { get; }
    public ImageSource? Icon { get; }
    public string Subtitle { get; }
    public string EnchantText { get; }
    public string ExceedText { get; }
    public string Requirements { get; }
    public IReadOnlyList<ItemStatLineVm> MainStats { get; }
    public IReadOnlyList<ItemStatLineVm> SubStats { get; }
    public string SubStatsHeader { get; }
    public IReadOnlyList<ItemStatLineVm> Manastones { get; }
    public string ManastoneHeader { get; }
    public IReadOnlyList<GodstoneVm> Godstones { get; }
    public string GodstoneHeader { get; }
    public bool HasSockets { get; }
    public bool HasGodstoneSlot { get; }
    public string SetText { get; }
    public string Costumes { get; }
    public string Sources { get; }
}
