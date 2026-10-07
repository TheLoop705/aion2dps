using Aion2Dps.App.Settings;

namespace Aion2Dps.App.Overlay;

/// <summary>Display options for <see cref="OverlayView"/> (a projection of <see cref="OverlaySettings"/> + appearance).</summary>
public sealed record OverlayViewOptions
{
    public RowSize RowSize { get; init; } = RowSize.Compact;
    public MeterView View { get; init; } = MeterView.Dps;
    public BarMode BarMode { get; init; } = BarMode.RelativeToTop;
    public BarStyle BarStyle { get; init; } = BarStyle.Gradient;
    public PvpSort PvpSort { get; init; } = PvpSort.Threat;
    public int MaxRows { get; init; } = 10;
    public bool ShowTotal { get; init; } = true;
    public bool ShowContribution { get; init; } = true;
    public bool ShowCritRate { get; init; }
    public bool ShowMaxHit { get; init; }
    public bool ShowGearScore { get; init; }
    public bool ShowRank { get; init; } = true;
    public bool ShowClassEmblem { get; init; } = true;
    public bool ShowColumnHeader { get; init; } = true;
    public double BackgroundOpacity { get; init; } = 0.92;
    public bool Locked { get; init; }
    public bool ClickThrough { get; init; }
    /// <summary>Only the local player and party-roster members are listed (engine option, mirrored here for the chip).</summary>
    public bool PartyOnly { get; init; }
    public string Version { get; init; } = "";

    public static OverlayViewOptions From(AppSettings s, string version) => new()
    {
        RowSize = s.Overlay.RowSize,
        View = s.Overlay.View,
        BarMode = s.Overlay.BarMode,
        BarStyle = s.Appearance.BarStyle,
        PvpSort = s.Overlay.PvpSort,
        MaxRows = s.Overlay.MaxRows,
        ShowTotal = s.Overlay.ShowTotal,
        ShowContribution = s.Overlay.ShowContribution,
        ShowCritRate = s.Overlay.ShowCritRate,
        ShowMaxHit = s.Overlay.ShowMaxHit,
        ShowGearScore = s.Overlay.ShowGearScore,
        ShowRank = s.Overlay.ShowRank,
        ShowClassEmblem = s.Overlay.ShowClassEmblem,
        ShowColumnHeader = s.Overlay.ShowColumnHeader,
        BackgroundOpacity = s.Overlay.BackgroundOpacity,
        Locked = s.Overlay.Locked,
        ClickThrough = s.Overlay.ClickThrough,
        PartyOnly = s.General.PartyOnly,
        Version = version,
    };
}

/// <summary>Non-engine state shown by the overlay (capture state, training countdown, transient flash text).</summary>
public sealed record OverlayStatus
{
    public CaptureStatus Capture { get; init; } = new() { State = CaptureState.Capturing };
    /// <summary>Remaining time of a running training run (null = not training).</summary>
    public TimeSpan? TrainingRemaining { get; init; }
    /// <summary>Short transient message in the footer (e.g. "Copied to clipboard").</summary>
    public string? Flash { get; init; }
    /// <summary>Entity id of the row pinned for ctrl+click comparison.</summary>
    public uint? PinnedEntityId { get; init; }
}

/// <summary>Per-row-size metrics.</summary>
internal readonly record struct RowMetrics(double Height, double FontSize, double NumberSize, double Emblem, double RankWidth)
{
    public static RowMetrics For(RowSize size) => size switch
    {
        RowSize.Normal => new RowMetrics(26, 12.5, 12.5, 18, 20),
        RowSize.Compact => new RowMetrics(21, 11.5, 11.5, 15, 18),
        _ => new RowMetrics(16, 10.5, 10.5, 0, 16),
    };

    public static double PvpHeight(RowSize size) => size switch
    {
        RowSize.Normal => 38,
        RowSize.Compact => 31,
        _ => 17,
    };
}

/// <summary>The overlay's high-level state (drives the header text and the empty-state panel).</summary>
public enum OverlayPhase
{
    NpcapMissing,
    CaptureError,
    CaptureStopped,
    WaitingForGame,
    Detecting,
    WaitingForData,
    WaitingForCombat,
    Live,
    Ended,
}

public static class OverlayPhaseLogic
{
    public static OverlayPhase Compute(CaptureStatus capture, MeterSnapshot snapshot)
    {
        switch (capture.State)
        {
            case CaptureState.NpcapMissing: return OverlayPhase.NpcapMissing;
            case CaptureState.Error: return OverlayPhase.CaptureError;
            // A finished replay/simulation keeps showing its last numbers.
            case CaptureState.Stopped when snapshot.State is not (MeterState.InCombat or MeterState.Ended): return OverlayPhase.CaptureStopped;
            case CaptureState.WaitingForGame: return OverlayPhase.WaitingForGame;
            case CaptureState.Detecting: return OverlayPhase.Detecting;
        }
        return snapshot.State switch
        {
            MeterState.Idle => OverlayPhase.WaitingForData,
            MeterState.WaitingForCombat => OverlayPhase.WaitingForCombat,
            MeterState.Ended => OverlayPhase.Ended,
            _ => OverlayPhase.Live,
        };
    }
}
