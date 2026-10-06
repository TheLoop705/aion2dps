using Xunit.Abstractions;

namespace Aion2Dps.Armory.Tests;

/// <summary>Opt-in smoke test against the real official site (AION2DPS_LIVE=1). Makes ~7 polite requests.</summary>
public class LiveTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task Global_europe_end_to_end()
    {
        using var client = new ArmoryClient();
        var servers = await client.GetServersAsync(ArmoryRegion.Europe);
        output.WriteLine($"servers: {servers.Count}");
        Assert.NotEmpty(servers);
        Assert.Contains(servers, s => s.ServerId is >= 1301 and <= 1399);

        var page = await client.SearchCharactersAsync(ArmoryRegion.Europe, "Luna");
        output.WriteLine($"search: {page.Items.Count} of {page.Total}");
        Assert.NotEmpty(page.Items);
        var hit = page.Items.OrderByDescending(r => r.Level).First();

        var info = await client.GetCharacterAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId);
        output.WriteLine($"info: Lv {info.Profile.Level} {info.Profile.Class} CP {info.Profile.CombatPower} IL {info.Profile.ItemLevel} " +
                         $"stats {info.Stats.Count} titles {info.Titles.Titles.Count} boards {info.DaevanionBoards.Count}");
        Assert.Equal(hit.ServerId, info.Profile.ServerId);
        Assert.NotEmpty(info.Stats);

        var eq = await client.GetEquipmentAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId);
        output.WriteLine($"equipment: {eq.Items.Count} items, {eq.Skills.Count} skills, pet {eq.Pet?.Name}, wing {eq.Wing?.Name}");
        Assert.NotEmpty(eq.Items);

        var item = await client.GetItemDetailAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId, eq.Items[0]);
        output.WriteLine($"item: {item.Name} +{item.EnchantLevel} main {item.MainStats.Count} sub {item.SubStats.Count} stones {item.Manastones.Count}");
        Assert.Equal(eq.Items[0].Id, item.Id);

        if (info.DaevanionBoards.Count > 0)
        {
            var board = await client.GetDaevanionAsync(ArmoryRegion.Europe, hit.ServerId, hit.CharacterId, info.DaevanionBoards[0].Id);
            output.WriteLine($"daevanion: {board.Nodes.Count} nodes, {board.OpenStatEffects.Count} stat effects");
            Assert.NotEmpty(board.Nodes);
        }
    }

    [LiveFact]
    public async Task Korea_and_taiwan_basic_lookup()
    {
        using var client = new ArmoryClient();
        foreach (var (region, keyword) in new[] { (ArmoryRegion.Korea, "루나"), (ArmoryRegion.Taiwan, "小") })
        {
            var servers = await client.GetServersAsync(region);
            output.WriteLine($"{region}: {servers.Count} servers");
            Assert.NotEmpty(servers);
            int? serverId = region == ArmoryRegion.Taiwan ? servers[0].ServerId : null;
            var page = await client.SearchCharactersAsync(region, keyword, serverId);
            output.WriteLine($"{region}: search {page.Items.Count} of {page.Total}");
            Assert.NotEmpty(page.Items);
            var hit = page.Items.OrderByDescending(r => r.Level).First();
            var info = await client.GetCharacterAsync(region, hit.ServerId, hit.CharacterId);
            var eq = await client.GetEquipmentAsync(region, hit.ServerId, hit.CharacterId);
            output.WriteLine($"{region}: Lv {info.Profile.Level} {info.Profile.Class} CP {info.Profile.CombatPower}, {eq.Items.Count} items");
            Assert.NotEmpty(eq.Items);
            var item = await client.GetItemDetailAsync(region, hit.ServerId, hit.CharacterId, eq.Items[0]);
            output.WriteLine($"{region}: item {item.Name} stones {item.Manastones.Count}");
        }
    }
}
