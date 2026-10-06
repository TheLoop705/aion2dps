using Aion2Dps.App.Controls;
using Aion2Dps.App.Infrastructure;
using Aion2Dps.App.Integration;

namespace Aion2Dps.App.Dashboard;

/// <summary>Saved fights (view supplied by <see cref="AnalysisBridge.CreateHistoryView"/>).</summary>
public sealed class HistoryPage : DashboardPage
{
    private readonly ContentControl _host = new();
    private DateTime _builtAt;

    public HistoryPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("History"));
        root.Children.Add(Ui.PageSubtitle("Every boss kill and wipe is saved automatically. Click a fight to open its report; right-click for more."));
        root.Children.Add(_host);
        Content = root;
    }

    public override string Key => "History";
    public override string Title => "History";
    public override string Icon => Ui.IconHistory;

    public override void OnShown()
    {
        // Rebuild when shown (cheap) so newly saved fights appear.
        if (_host.Content is null || (DateTime.UtcNow - _builtAt).TotalSeconds > 2)
        {
            _host.Content = AnalysisBridge.CreateHistoryView(Context.Services.Store, Context.Services.GameData);
            _builtAt = DateTime.UtcNow;
        }
    }
}

/// <summary>Per-boss trends (view supplied by <see cref="AnalysisBridge.CreateTrendsView"/>).</summary>
public sealed class TrendsPage : DashboardPage
{
    private readonly ContentControl _host = new();

    public TrendsPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Trends"));
        root.Children.Add(Ui.PageSubtitle("Your DPS per boss over time: best, median, last and fastest kill."));
        root.Children.Add(_host);
        Content = root;
    }

    public override string Key => "Trends";
    public override string Title => "Trends";
    public override string Icon => Ui.IconTrends;

    public override void OnShown() => _host.Content = AnalysisBridge.CreateTrendsView(Context.Services.Store, Context.Services.GameData);
}

/// <summary>Own character (from the engine) plus the Armory lookup view.</summary>
public sealed class CharacterPage : DashboardPage
{
    private readonly ContentControl _armory = new();
    private readonly ClassEmblem _emblem = new(44);
    private readonly TextBlock _name = Ui.Text("", ThemeKeys.Text, 20, FontWeights.SemiBold);
    private readonly TextBlock _details = Ui.Text("", ThemeKeys.TextMuted, 12.5);

    public CharacterPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Character"));
        root.Children.Add(Ui.PageSubtitle("Your character as detected from the game's login data, and character lookup."));
        var own = new StackPanel { Orientation = Orientation.Horizontal };
        _emblem.Margin = new Thickness(0, 0, 14, 0);
        own.Children.Add(_emblem);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_name);
        text.Children.Add(_details);
        own.Children.Add(text);
        root.Children.Add(Ui.Card("Your character", own));
        root.Children.Add(_armory);
        Content = root;
    }

    public override string Key => "Character";
    public override string Title => "Character";
    public override string Icon => Ui.IconCharacter;

    public override void OnShown()
    {
        _armory.Content ??= AnalysisBridge.CreateArmoryView();
    }

    public override void Refresh()
    {
        var lp = Context.Services.Engine.LocalPlayer;
        var gd = Context.Services.GameData;
        if (lp is null)
        {
            _emblem.Class = CharacterClass.Unknown;
            _name.Text = "Not detected yet";
            _details.Text = "Log in (or change zone) while Aion2Dps is running; name and class are read from the login data.";
            return;
        }
        _emblem.Class = lp.Class;
        _name.Text = lp.Name;
        _details.Text = $"{gd.GetClassName(lp.Class)} · Level {lp.Level} · {gd.GetServerName(lp.ServerId) ?? "Server " + lp.ServerId}";
    }
}

/// <summary>Version, license, data credits and disclaimer.</summary>
public sealed class AboutPage : DashboardPage
{
    public AboutPage(DashboardContext context) : base(context)
    {
        var root = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Image { Source = AppIcon.ImageSource, Width = 56, Height = 56 });
        var ht = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(Ui.Text("Aion2Dps", ThemeKeys.Text, 24, FontWeights.Bold));
        ht.Children.Add(Ui.Text($"Version {AppPaths.Version} · {context.Services.ModeLabel} mode · .NET {Environment.Version}", ThemeKeys.TextMuted, 12.5));
        head.Children.Add(ht);
        root.Children.Add(head);

        root.Children.Add(Ui.Card("License", Wrap(
            "Aion2Dps is free software licensed under the GNU General Public License v3.0 or later (GPL-3.0-or-later). " +
            "It comes with ABSOLUTELY NO WARRANTY. The application code was written from scratch from this project's protocol notes.")));

        root.Children.Add(Ui.Card("Data credits", Wrap(ReadNotice())));

        root.Children.Add(Ui.Card("Disclaimer", Wrap(
            "A personal, passive tool: it reads a copy of the game's own server-to-client network traffic through Npcap. " +
            "It does not read or modify game memory, inject code, automate input or send anything to the game servers. " +
            "AION 2 and its game data are © NCSOFT. This project is not affiliated with or endorsed by NCSOFT. Use at your own risk.")));

        var paths = Ui.KeyValueGrid(["Settings & logs", "Recordings", "Game data"], out var values, 130);
        values["Settings & logs"].Text = AppPaths.DataDirectory;
        values["Recordings"].Text = context.CaptureFolder;
        values["Game data"].Text = AppPaths.GameDataDirectory;
        root.Children.Add(Ui.Card("Folders", paths));
        Content = root;
    }

    public override string Key => "About";
    public override string Title => "About";
    public override string Icon => Ui.IconAbout;

    private static TextBlock Wrap(string text)
    {
        var t = Ui.Text(text, ThemeKeys.Text, 12.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.LineHeight = 19;
        return t;
    }

    /// <summary>data/NOTICE.txt as plain text (tables become "file — source" lines).</summary>
    internal static string ReadNotice()
    {
        try
        {
            var path = Path.Combine(AppPaths.GameDataDirectory, "NOTICE.txt");
            if (!File.Exists(path)) return "Game data tables © NCSOFT, compiled by open-source AION 2 meters released under GPL-3.0 (see data/NOTICE.txt).";
            var lines = new List<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("|---", StringComparison.Ordinal) || l.StartsWith("| File", StringComparison.Ordinal)) continue;
                if (l.StartsWith('#')) continue;
                if (l.StartsWith('|'))
                {
                    var cells = l.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.Length >= 2) l = "• " + StripMd(cells[0]) + " — " + StripMd(cells[1]);
                }
                else l = StripMd(l);
                lines.Add(l);
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return "Could not read data/NOTICE.txt: " + ex.Message;
        }
    }

    private static string StripMd(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return s.Replace("`", "").Replace("**", "");
    }
}
