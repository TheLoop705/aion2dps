using Aion2Dps.App.Formatting;
using Aion2Dps.Contracts;

namespace Aion2Dps.App.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(999.6, "1.00K")]
    [InlineData(1_234, "1.23K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(123_456, "123K")]
    [InlineData(999_999, "1.00M")]
    [InlineData(4_560_000, "4.56M")]
    [InlineData(45_600_000, "45.6M")]
    [InlineData(456_000_000, "456M")]
    [InlineData(1_230_000_000, "1.23B")]
    [InlineData(-12_345, "-12.3K")]
    public void Abbrev_uses_three_significant_digits(double value, string expected) =>
        Assert.Equal(expected, Fmt.Abbrev(value));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(554_612, "554.6K")]
    [InlineData(1_234_567, "1.2M")]
    [InlineData(999_960, "1.0M")]
    public void Abbrev1_uses_one_decimal(double value, string expected) => Assert.Equal(expected, Fmt.Abbrev1(value));

    [Fact]
    public void Exact_groups_digits_culture_invariant()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1,002,252", Fmt.Exact(1_002_252L));
            Assert.Equal("1,002,252", Fmt.Exact(1_002_252.4));
            Assert.Equal("19.2%", Fmt.Percent(0.192));
            Assert.Equal("12.3K", Fmt.Abbrev(12_345));
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Unmeasured_values_are_dashes_never_zero_percent()
    {
        Assert.Equal(Fmt.Dash, Fmt.Rate(0, 0));
        Assert.Equal(Fmt.Dash, Fmt.Percent(null));
        Assert.Equal(Fmt.Dash, Fmt.Abbrev(double.NaN));
        Assert.Equal("0.0%", Fmt.Rate(0, 10));
        Assert.Equal("25.0%", Fmt.Rate(1, 4));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7.9, "0:07")]
    [InlineData(225, "3:45")]
    [InlineData(3723, "1:02:03")]
    public void Duration_formats(double seconds, string expected) => Assert.Equal(expected, Fmt.Duration(seconds));

    [Fact]
    public void HpText_matches_the_header_format()
    {
        Assert.Equal("1,002,252 HP · 19.2%", Fmt.HpText(1_002_252, 5_220_000, null));
        Assert.Equal("1,002,252 HP · 19.2%", Fmt.HpText(1_002_252, null, 0.192));
        Assert.Equal(Fmt.Dash, Fmt.HpText(null, null, null));
    }

    [Fact]
    public void Ping_and_bytes()
    {
        Assert.Equal("38 ms", Fmt.Ping(37.6));
        Assert.Equal(Fmt.Dash, Fmt.Ping(null));
        Assert.Equal("512 B", Fmt.Bytes(512));
        Assert.Equal("1.50 KB", Fmt.Bytes(1536));
    }
}

public class ChatSummaryTests
{
    private static MeterSnapshot Snapshot(int players, string boss = "Warden", int nameLength = 8) => new()
    {
        State = MeterState.InCombat,
        Mode = MeterMode.BossOnly,
        Elapsed = TimeSpan.FromSeconds(225),
        Target = new TargetInfo { Name = boss, IsBoss = true },
        Rows = Enumerable.Range(0, players).Select(i => new PlayerRow
        {
            EntityId = (uint)(i + 1),
            Name = new string((char)('A' + i % 26), nameLength),
            Damage = 1_000_000 - i * 1000,
            Dps = 554_612 - i * 10_000,
            Contribution = 0.348 - i * 0.01,
        }).ToList(),
    };

    [Fact]
    public void Formats_the_example_line()
    {
        var line = ChatSummary.Format("Boss", TimeSpan.FromSeconds(225),
            [new ChatSummary.Entry("Name", 554_600, 0.348), new ChatSummary.Entry("Name2", 484_900, 0.304)]);
        Assert.Equal("[Boss] 3:45 · Name 554.6K (34.8%) · Name2 484.9K (30.4%)", line);
    }

    [Fact]
    public void Orders_by_dps_and_uses_target_name()
    {
        var snap = Snapshot(3) with
        {
            Rows = [new PlayerRow { Name = "Low", Damage = 10, Dps = 100 }, new PlayerRow { Name = "High", Damage = 20, Dps = 900, Contribution = 0.5 }],
        };
        Assert.Equal("[Warden] 3:45 · High 900 (50.0%) · Low 100", ChatSummary.FromSnapshot(snap));
    }

    [Fact]
    public void Never_exceeds_255_characters_and_summarises_the_rest()
    {
        var line = ChatSummary.FromSnapshot(Snapshot(10, new string('B', 80), nameLength: 24))!;
        Assert.True(line.Length <= ChatSummary.MaxChatLength, $"{line.Length}: {line}");
        Assert.Matches(@"· \+\d+$", line);
        Assert.Contains("…]", line); // long boss names are shortened
    }

    [Fact]
    public void Small_party_fits_without_tail()
    {
        var line = ChatSummary.FromSnapshot(Snapshot(5))!;
        Assert.DoesNotContain("· +", line);
        Assert.Equal(6, line.Split(" · ").Length);
    }

    [Fact]
    public void Returns_null_without_damage()
    {
        Assert.Null(ChatSummary.FromSnapshot(MeterSnapshot.Empty));
    }

    [Fact]
    public void Pvp_line_lists_opponents()
    {
        var snap = new MeterSnapshot
        {
            Mode = MeterMode.Pvp,
            Elapsed = TimeSpan.FromSeconds(70),
            PvpKills = 1,
            PvpRows = [new PvpRow { Name = "Draven", DamageDealt = 1_200_000, DamageTaken = 845_000 }, new PvpRow { Name = "Mael", DamageDealt = 400_000, Killed = true }],
        };
        var line = ChatSummary.FromSnapshot(snap)!;
        Assert.StartsWith("[PvP] 1:10 · 1 kill · dealt 1.6M · taken 845.0K", line);
        Assert.Contains("Draven ↑1.2M ↓845.0K", line);
        Assert.Contains("Mael ↑400.0K ↓0 KO", line);
    }
}
