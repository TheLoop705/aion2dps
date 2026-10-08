using System.Windows.Controls.Primitives;
using Aion2Dps.App.Controls;
using Aion2Dps.App.Formatting;

namespace Aion2Dps.App.Dashboard;

/// <summary>Live status, capture status (adapter/server/packets/gaps, recording), protocol diagnostics and the opcode census.</summary>
public sealed class MeterPage : DashboardPage
{
    private readonly Dictionary<string, TextBlock> _live;
    private readonly Dictionary<string, TextBlock> _capture;
    private readonly Dictionary<string, TextBlock> _proto;
    private readonly Grid _census = new();
    private readonly StackPanel _errors = new();
    private readonly Button _recordButton;
    private readonly TextBlock _captureMessage = Ui.Text("", ThemeKeys.TextMuted, 12);

    public MeterPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Meter"));
        root.Children.Add(Ui.PageSubtitle("Live encounter, capture and protocol health. Everything here is read-only: the meter only listens to the game's own traffic."));

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new StackPanel();
        var liveGrid = Ui.KeyValueGrid(["State", "Mode", "Encounter", "Target", "Elapsed", "Party DPS", "Your DPS", "Character", "Ping"], out _live, 120);
        var liveButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        liveButtons.Children.Add(Ui.Button("Toggle overlay", () => Context.ToggleOverlay(), icon: ""));
        liveButtons.Children.Add(Ui.Button("Reset meter", () => Context.Services.Engine.Reset(), icon: Ui.IconReset));
        var liveStack = new StackPanel();
        liveStack.Children.Add(liveGrid);
        liveStack.Children.Add(liveButtons);
        left.Children.Add(Ui.Card("Live", liveStack));

        var capStack = new StackPanel();
        _captureMessage.TextWrapping = TextWrapping.Wrap;
        _captureMessage.Margin = new Thickness(0, 0, 0, 8);
        capStack.Children.Add(_captureMessage);
        capStack.Children.Add(Ui.KeyValueGrid(["State", "Adapter", "Local endpoint", "Server", "Game process", "Packets", "Delivered", "TCP gaps", "Last packet", "Recording"], out _capture, 120));
        var capButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _recordButton = Ui.Button("Start recording", ToggleRecording, icon: Ui.IconRecord);
        capButtons.Children.Add(_recordButton);
        capButtons.Children.Add(Ui.Button("Captures folder", () => { Directory.CreateDirectory(Context.CaptureFolder); Ui.OpenUrl(Context.CaptureFolder); }, icon: Ui.IconFolder));
        capButtons.Children.Add(Ui.Button("Restart capture", () => Context.RestartCapture(), icon: Ui.IconReset));
        capStack.Children.Add(capButtons);
        left.Children.Add(Ui.Card("Capture", capStack, "Recordings (.pcapng) contain only the locked game connection and are saved to Documents\\Aion2Dps\\captures."));

        var right = new StackPanel();
        right.Children.Add(Ui.Card("Protocol", Ui.KeyValueGrid(["Bytes in", "Frames", "Bundles", "Bundle errors", "Resyncs", "Decode errors", "Events", "Last frame", "HP check"], out _proto, 120)));
        var censusStack = new StackPanel();
        censusStack.Children.Add(_census);
        right.Children.Add(Ui.Card("Opcode census", censusStack, "Per-opcode traffic since start. Unknown opcodes have no decoder yet."));
        right.Children.Add(Ui.Card("Recent errors", _errors));

        cols.Children.Add(left);
        Grid.SetColumn(right, 2);
        cols.Children.Add(right);
        root.Children.Add(cols);
        Content = root;
    }

    public override string Key => "Meter";
    public override string Title => "Meter";
    public override string Icon => Ui.IconMeter;

    private void ToggleRecording()
    {
        var cap = Context.Services.Capture;
        try
        {
            if (cap.Status.RecordingPath is null)
            {
                Directory.CreateDirectory(Context.CaptureFolder);
                var path = Path.Combine(Context.CaptureFolder, $"aion2-{DateTime.Now:yyyyMMdd-HHmmss}.pcapng");
                cap.StartRecording(path);
                AppLog.Info("Dashboard", $"Recording to {path}");
            }
            else cap.StopRecording();
        }
        catch (Exception ex)
        {
            AppLog.Error("Dashboard", "Recording failed", ex);
            MessageBox.Show("Recording failed: " + ex.Message, "Aion2Dps", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    public override void Refresh()
    {
        var s = Context.Services;
        var snap = s.Engine.GetSnapshot(s.Now());
        var local = snap.Rows.FirstOrDefault(r => r.IsLocal);
        _live["State"].Text = snap.State switch
        {
            MeterState.InCombat => "In combat",
            MeterState.Ended => $"Ended ({snap.Outcome})",
            MeterState.WaitingForCombat => "Waiting for combat",
            _ => "Idle",
        };
        _live["Mode"].Text = snap.Mode switch { MeterMode.AllTargets => "All targets", MeterMode.Pvp => "PvP", _ => "Boss only" } + $" · {s.ModeLabel}";
        _live["Encounter"].Text = snap.EncounterKind?.ToString() ?? Fmt.Dash;
        _live["Target"].Text = snap.Target is { } t ? $"{t.Name} · {Fmt.HpText(t.Hp, t.MaxHp, t.HpFraction)}" : Fmt.Dash;
        _live["Elapsed"].Text = snap.State is MeterState.InCombat or MeterState.Ended ? Fmt.Duration(snap.Elapsed) : Fmt.Dash;
        _live["Party DPS"].Text = snap.PartyDps > 0 ? $"{Fmt.Abbrev(snap.PartyDps)}  ({Fmt.Exact(snap.PartyDps)})" : Fmt.Dash;
        _live["Your DPS"].Text = local is not null && local.Damage > 0 ? $"{Fmt.Abbrev(local.Dps)}  ({Fmt.Percent(local.Contribution)} contribution)" : Fmt.Dash;
        var lp = s.Engine.LocalPlayer;
        _live["Character"].Text = lp is null ? "Not detected yet" : $"{lp.Name} · {s.GameData.GetClassName(lp.Class)} · Lv {lp.Level} · {s.GameData.GetServerName(lp.ServerId) ?? lp.ServerId.ToString(CultureInfo.InvariantCulture)}";
        _live["Ping"].Text = Fmt.Ping(snap.PingMs);

        var c = s.Capture.Status;
        _captureMessage.Text = c.Message;
        _capture["State"].Text = c.State.ToString();
        _capture["Adapter"].Text = c.AdapterDescription ?? c.AdapterName ?? (s.Capture.AdapterOverride is null ? "Automatic" : s.Capture.AdapterOverride);
        _capture["Local endpoint"].Text = c.LocalEndpoint ?? Fmt.Dash;
        _capture["Server"].Text = c.ServerEndpoint ?? Fmt.Dash;
        _capture["Game process"].Text = c.GameProcessId?.ToString(CultureInfo.InvariantCulture) ?? Fmt.Dash;
        _capture["Packets"].Text = Fmt.Exact(c.PacketsSeen);
        _capture["Delivered"].Text = Fmt.Bytes(c.BytesDelivered);
        _capture["TCP gaps"].Text = Fmt.Exact(c.GapCount);
        _capture["TCP gaps"].Ref(TextBlock.ForegroundProperty, c.GapCount > 0 ? ThemeKeys.Warning : ThemeKeys.Text);
        _capture["Last packet"].Text = c.LastPacketUtc is { } lpk ? Ago(lpk) : Fmt.Dash;
        _capture["Recording"].Text = c.RecordingPath is null ? "Off" : Path.GetFileName(c.RecordingPath);
        _capture["Recording"].ToolTip = c.RecordingPath;
        _recordButton.Content = MakeButtonContent(c.RecordingPath is null ? "Start recording" : "Stop recording", c.RecordingPath is null ? Ui.IconRecord : Ui.IconStop);

        var d = s.Diagnostics;
        if (d is null)
        {
            foreach (var v in _proto.Values) v.Text = Fmt.Dash;
            _proto["Frames"].Text = "Diagnostics unavailable in this mode";
        }
        else
        {
            _proto["Bytes in"].Text = Fmt.Bytes(d.BytesIn);
            _proto["Frames"].Text = Fmt.Exact(d.Frames);
            _proto["Bundles"].Text = Fmt.Exact(d.Bundles);
            _proto["Bundle errors"].Text = Fmt.Exact(d.BundleErrors);
            _proto["Resyncs"].Text = Fmt.Exact(d.Resyncs);
            _proto["Decode errors"].Text = Fmt.Exact(d.DecodeErrors);
            _proto["Decode errors"].Ref(TextBlock.ForegroundProperty, d.DecodeErrors > 0 ? ThemeKeys.Warning : ThemeKeys.Text);
            _proto["Events"].Text = Fmt.Exact(d.EventsEmitted);
            _proto["Last frame"].Text = d.LastFrameUtc is { } lf ? Ago(lf) : Fmt.Dash;
            UpdateCensus(d.GetCensus());
            UpdateErrors(d.GetRecentErrors());
        }
        _proto["HP check"].Text = snap.PartialView
            ? $"partial view{(snap.HpCheckRatio is { } pr ? $"  ({Fmt.Percent(pr, 1)} of the boss HP loss is visible)" : "")}"
            : snap.HpCheckRatio is { } r ? $"{Fmt.Percent(r, 2)}  {(Math.Abs(r - 1) <= 0.01 ? "✓ matches boss HP" : "✗ mismatch")}" : Fmt.Dash;
        _proto["HP check"].Ref(TextBlock.ForegroundProperty, !snap.PartialView && snap.HpCheckRatio is { } r2 && Math.Abs(r2 - 1) > 0.03 ? ThemeKeys.Warning : ThemeKeys.Text);
    }

    private string Ago(DateTime utc) =>
        Math.Max(0, (Context.Services.Now() - utc).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s ago";

    private static object MakeButtonContent(string text, string icon)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void UpdateCensus(IReadOnlyList<OpcodeStat> census)
    {
        _census.Children.Clear();
        _census.RowDefinitions.Clear();
        if (_census.ColumnDefinitions.Count == 0)
            foreach (var w in new[] { 64.0, -1, 80, 80, 64 })
                _census.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Row(int r, bool header, string op, string count, string bytes, string decoded, string failed, bool unknown)
        {
            _census.RowDefinitions.Add(new RowDefinition { Height = new GridLength(header ? 20 : 21) });
            var cells = new[] { op, count, bytes, decoded, failed };
            for (int i = 0; i < cells.Length; i++)
            {
                string key = header ? ThemeKeys.TextMuted : i == 0 ? (unknown ? ThemeKeys.TextMuted : ThemeKeys.Accent) : i == 4 && failed != "0" ? ThemeKeys.Warning : ThemeKeys.Text;
                var t = Ui.Text(cells[i], key, header ? 10 : 12, header ? FontWeights.Bold : FontWeights.Normal, mono: !header);
                t.TextAlignment = i == 0 ? TextAlignment.Left : TextAlignment.Right;
                Grid.SetRow(t, r);
                Grid.SetColumn(t, i);
                _census.Children.Add(t);
            }
        }
        Row(0, true, "OPCODE", "COUNT", "BYTES", "DECODED", "FAILED", false);
        int row = 1;
        foreach (var s in census.Take(24))
        {
            bool unknown = s.Decoded == 0 && s.Failed == 0;
            Row(row++, false, s.Hex, Fmt.Exact(s.Count), Fmt.Bytes(s.Bytes), unknown ? Fmt.Dash : Fmt.Exact(s.Decoded), unknown ? Fmt.Dash : Fmt.Exact(s.Failed), unknown);
        }
    }

    private void UpdateErrors(IReadOnlyList<string> errors)
    {
        _errors.Children.Clear();
        var lines = errors.TakeLast(8).ToList();
        if (Context.Log is { } log)
            lines.AddRange(log.Recent.Where(l => l.Contains("[Error]") || l.Contains("[Warn ]")).TakeLast(Math.Max(0, 8 - lines.Count)));
        if (lines.Count == 0)
        {
            _errors.Children.Add(Ui.Text("No errors.", ThemeKeys.TextMuted, 12));
            return;
        }
        foreach (var l in lines)
        {
            var t = Ui.Text(l, ThemeKeys.TextMuted, 11, mono: true);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            t.Margin = new Thickness(0, 1, 0, 1);
            _errors.Children.Add(t);
        }
    }
}
