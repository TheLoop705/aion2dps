using Aion2Dps.Contracts;

namespace Aion2Dps.Armory.Tests;

public class ParserTests
{
    [Fact]
    public void Servers_api_fixture_parses_both_factions()
    {
        var list = ArmoryParser.ParseServers(Fixtures.Read("servers_global_eu.json"), ArmoryRegion.Europe);
        Assert.Equal(44, list.Count);
        Assert.Equal(22, list.Count(s => s.Faction == Faction.Elyos));
        Assert.Equal(22, list.Count(s => s.Faction == Faction.Asmodian));
        var first = list[0];
        Assert.Equal(1301, first.ServerId);
        Assert.Equal("Siel", first.Name);
        Assert.Equal("SIE", first.ShortName);
        Assert.Equal(ArmoryRegion.Europe, first.Region);
        Assert.Equal("Siel (Elyos)", first.DisplayName);
        Assert.All(list, s => Assert.InRange(s.ServerId % 1000, 300, 399));
    }

    [Fact]
    public void Server_map_from_global_page_is_filtered_by_region()
    {
        var html = Fixtures.Read("characters_index_global.html");
        var eu = ArmoryParser.ParseServerMapFromHtml(html, ArmoryRegion.Europe);
        var nae = ArmoryParser.ParseServerMapFromHtml(html, ArmoryRegion.NorthAmericaEast);
        Assert.Equal(44, eu.Count);
        Assert.Equal(16, nae.Count);
        Assert.All(nae, s => Assert.Equal(1, s.ServerId / 100 % 10));
        Assert.Equal(ArmoryParser.ParseServers(Fixtures.Read("servers_global_eu.json"), ArmoryRegion.Europe).Select(s => s.ServerId),
            eu.Select(s => s.ServerId));
    }

    [Fact]
    public void Server_map_from_korean_page_parses_all_entries()
    {
        var kr = ArmoryParser.ParseServerMapFromHtml(Fixtures.Read("characters_index_kr.html"), ArmoryRegion.Korea);
        Assert.Equal(42, kr.Count);
        Assert.Equal(1001, kr[0].ServerId);
        Assert.Equal("시엘", kr[0].Name);
    }

    [Fact]
    public void Server_map_missing_is_endpoint_changed()
    {
        var ex = Assert.Throws<ArmoryException>(() => ArmoryParser.ParseServerMapFromHtml("<html><body>maintenance</body></html>", ArmoryRegion.Korea));
        Assert.Equal(ArmoryErrorKind.EndpointChanged, ex.Kind);
    }

    [Fact]
    public void Classes_and_pcdata_parse()
    {
        var classes = ArmoryParser.ParseClasses(Fixtures.Read("classes_global.json"));
        Assert.Equal(8, classes.Count);
        Assert.Contains(classes, c => c.Name == "Elementalist" && c.Text == "Spiritmaster");

        var pc = ArmoryParser.ParsePcData(Fixtures.Read("pcdata_global.json"));
        Assert.Equal(32, pc.Count);
        // The built-in pcId table must agree with the live pcdata list.
        Assert.All(pc, p => Assert.Equal(ArmorySlots.ClassFromName(p.ClassName), ArmorySlots.ClassFromPcId(p.PcId)));
    }

    [Fact]
    public void Search_fixture_strips_highlights_and_decodes_ids()
    {
        var page = ArmoryParser.ParseSearch(Fixtures.Read("search_global_eu.json"), ArmoryRegion.Europe);
        Assert.Equal(5, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(2777, page.Total);
        Assert.Equal(556, page.EndPage);
        Assert.True(page.HasMore);

        var r = page.Items[0];
        Assert.Equal("tester", r.Name);
        Assert.EndsWith("00=", r.CharacterId);
        Assert.DoesNotContain("%", r.CharacterId);
        Assert.Equal(Faction.Asmodian, r.Faction);
        Assert.Equal(16, r.PcId);
        Assert.Equal(CharacterClass.Ranger, r.Class);
        Assert.Equal(22, r.Level);
        Assert.Equal(2305, r.ServerId);
        Assert.Equal("Marchutan", r.ServerName);
        Assert.Equal("eu", r.RegionCode);
        Assert.StartsWith("https://profileimg.plaync.com/game_profile_images/", r.ProfileImageUrl);

        Assert.Equal("Testerx", page.Items[2].Name);
        Assert.Equal("Tester&Co", page.Items[4].Name);
    }

    [Fact]
    public void Character_info_fixture_parses_profile_stats_titles_and_boards()
    {
        var info = ArmoryParser.ParseCharacterInfo(Fixtures.Read("character_info_global.json"), ArmoryRegion.Europe);
        var p = info.Profile;
        Assert.Equal("Tester", p.Name);
        Assert.Equal(45, p.Level);
        Assert.Equal(CharacterClass.Sorcerer, p.Class);
        Assert.Equal("Sorcerer", p.ClassName);
        Assert.Equal(35181, p.CombatPower);
        Assert.Equal(797, p.ItemLevel);
        Assert.Equal(Faction.Elyos, p.Faction);
        Assert.Equal(1307, p.ServerId);
        Assert.Equal("Fregion", p.ServerName);
        Assert.Equal("Tamed", p.TitleName);
        Assert.Equal("Common", p.TitleGrade);
        Assert.Null(p.LegionName);
        Assert.Null(p.RegionName); // sent as ""

        Assert.Equal(17, info.Stats.Count);
        Assert.Equal(6, info.Stats.Count(s => s.Category == StatCategory.Core));
        Assert.Equal(10, info.Stats.Count(s => s.Category == StatCategory.Pantheon));
        var might = info.Stats.Single(s => s.Type == "STR");
        Assert.Equal("Might", might.Name);
        Assert.Equal(15, might.Value);
        Assert.Equal(["Attack increase +1.5%"], might.Effects);
        Assert.Empty(info.Stats.Single(s => s.Type == "DEX").Effects); // statSecondList: null
        Assert.Equal("Item Level", info.Stats.Single(s => s.Category == StatCategory.ItemLevel).Name);

        Assert.Equal(297, info.Titles.TotalCount);
        Assert.Equal(41, info.Titles.OwnedCount);
        Assert.Equal(3, info.Titles.Titles.Count);
        var t = info.Titles.Titles[0];
        Assert.Equal("Draped in Sky", t.Name);
        Assert.Equal("Attack", t.Category);
        Assert.Equal(["Attack Bonus +5"], t.EquipStats);
        Assert.Equal(["Accuracy Bonus +5"], t.Stats);

        Assert.Empty(info.Rankings); // rankingList: null on Global
        Assert.Equal(5, info.DaevanionBoards.Count);
        var b = info.DaevanionBoards[0];
        Assert.Equal((61, "Nezekan", 30, 88), (b.Id, b.Name, b.OpenNodes, b.TotalNodes));
        Assert.InRange(b.Percent, 34.0, 34.2);
    }

    [Fact]
    public void Korean_character_info_uses_pcid_for_class()
    {
        var info = ArmoryParser.ParseCharacterInfo(Fixtures.Read("character_info_kr.json"), ArmoryRegion.Korea);
        Assert.Equal("테스터", info.Profile.Name);
        Assert.Equal("마도성", info.Profile.ClassName);
        Assert.Equal(CharacterClass.Sorcerer, info.Profile.Class);
        Assert.Equal(292421, info.Profile.CombatPower);
    }

    [Fact]
    public void Equipment_fixture_parses_gear_pet_wings_and_skills()
    {
        var eq = ArmoryParser.ParseEquipment(Fixtures.Read("equipment_global.json"));
        Assert.Equal(18, eq.Items.Count);
        Assert.Equal(6, eq.Skins.Count);
        var main = eq.Items[0];
        Assert.Equal(1, main.SlotPos);
        Assert.Equal("MainHand", main.SlotName);
        Assert.Equal("Spiritforged Spellbook", main.Name);
        Assert.Equal(7, main.EnchantLevel);
        Assert.Equal(ItemGrade.Unique, main.Grade);
        Assert.Equal(SlotGroup.Weapon, main.Group);
        Assert.Equal(110530047, main.Id);
        Assert.Equal(ItemGrade.Rare, eq.Items.Single(i => i.SlotName == "Belt").Grade);
        Assert.Equal(SlotGroup.Armor, eq.Items.Single(i => i.SlotName == "Cape").Group);
        Assert.Equal(SlotGroup.Accessory, eq.Items.Single(i => i.SlotName == "Amulet").Group);
        // cape sorts right after boots
        var order = eq.Items.Select(i => i.SlotName).ToList();
        Assert.Equal(order.IndexOf("Boots") + 1, order.IndexOf("Cape"));

        Assert.NotNull(eq.Pet);
        Assert.Equal("Dark Spirit", eq.Pet!.Name);
        Assert.Equal(2, eq.Pet.Level);
        Assert.NotNull(eq.Wing);
        Assert.Equal("Superior Daeva Wings", eq.Wing!.Name);
        Assert.Equal(ItemGrade.Legend, eq.Wing.Grade);
        Assert.Null(eq.WingSkin); // all-null object → no wing skin

        Assert.Equal(35, eq.Skills.Count);
        var s = eq.Skills[0];
        Assert.Equal("Flame Arrow", s.Name);
        Assert.Equal(11, s.Level);
        Assert.Equal("Active", s.Category);
        Assert.True(s.Equipped);
    }

    [Fact]
    public void Korean_equipment_has_arcana_and_runes()
    {
        var eq = ArmoryParser.ParseEquipment(Fixtures.Read("equipment_kr.json"));
        Assert.Equal(26, eq.Items.Count);
        Assert.Equal(6, eq.Items.Count(i => i.Group == SlotGroup.Arcana));
        Assert.Equal(2, eq.Items.Count(i => i.Group == SlotGroup.Rune));
        Assert.Equal(5, eq.Items.Single(i => i.SlotPos == 1).ExceedLevel);
        Assert.Equal(ItemGrade.Special, eq.Items.Single(i => i.SlotPos == 23).Grade);
        Assert.NotNull(eq.WingSkin);
    }

    [Fact]
    public void Item_detail_fixture_parses_stats()
    {
        var d = ArmoryParser.ParseItemDetail(Fixtures.Read("item_detail_global.json"));
        Assert.Equal(110530047, d.Id);
        Assert.Equal("Spiritforged Spellbook", d.Name);
        Assert.Equal(ItemGrade.Unique, d.Grade);
        Assert.Equal(7, d.EnchantLevel);
        Assert.Equal(15, d.MaxEnchantLevel);
        Assert.Null(d.MaxExceedLevel);
        Assert.Equal("Spellbook", d.CategoryName);
        Assert.Equal(["Sorcerer"], d.ClassNames);
        Assert.Equal(3, d.MainStats.Count);
        var atk = d.MainStats[0];
        Assert.Equal(("WeaponFixingDamage", "Attack", "229", "206", "35", false), (atk.Id, atk.Name, atk.Value, atk.MinValue, atk.Extra, atk.Exceed));
        Assert.Equal(4, d.SubStats.Count);
        Assert.Equal(4, d.MagicStoneSlotCount);
        Assert.Equal(1, d.GodStoneSlotCount);
        Assert.Empty(d.Manastones);
        Assert.Equal(["Quest"], d.Sources);
        Assert.Equal(["Golden Vow (Spellbook)"], d.Costumes);
    }

    [Fact]
    public void Item_detail_with_sockets_parses_manastones_and_godstone()
    {
        var d = ArmoryParser.ParseItemDetail(Fixtures.Read("item_detail_sockets_kr.json"));
        Assert.Equal(15, d.EnchantLevel);
        Assert.Equal(5, d.MaxExceedLevel);
        Assert.Equal(5, d.SubStats.Count);
        Assert.Equal("13.4%", d.SubStats[0].Value);
        Assert.Equal(4, d.Manastones.Count);
        Assert.Equal(("AmplifyAllDamage", "+100", ItemGrade.Unique, 0), (d.Manastones[0].Id, d.Manastones[0].Value, d.Manastones[0].Grade, d.Manastones[0].SlotPos));
        Assert.Equal(ItemGrade.Rare, d.Manastones[3].Grade);
        var g = Assert.Single(d.Godstones);
        Assert.Equal(ItemGrade.Legend, g.Grade);
        Assert.Contains("\n", g.Description);
        Assert.Equal("100", d.SoulBindRate);
    }

    [Fact]
    public void Item_detail_wrapped_in_data_is_accepted()
    {
        var d = ArmoryParser.ParseItemDetail("{\"data\":" + Fixtures.Read("item_detail_global.json") + "}");
        Assert.Equal(110530047, d.Id);
    }

    [Fact]
    public void Daevanion_fixture_parses_nodes_and_effects()
    {
        var d = ArmoryParser.ParseDaevanion(Fixtures.Read("daevanion_detail_global.json"), 61);
        Assert.Equal(61, d.BoardId);
        Assert.Equal(225, d.Nodes.Count);
        Assert.Equal(89, d.Nodes.Count(n => n.Type != DaevanionNodeType.None));
        Assert.Equal(30, d.Nodes.Count(n => n.Open));
        Assert.Contains(d.Nodes, n => n.Type == DaevanionNodeType.Start);
        Assert.Contains(d.Nodes, n => n.Type == DaevanionNodeType.SkillLevel);
        Assert.Equal(6, d.OpenStatEffects.Count);
        Assert.Equal("HP +700", d.OpenStatEffects[0]);
        Assert.Equal(6, d.OpenSkillEffects.Count);
        // sorted by row then col
        Assert.True(d.Nodes.Zip(d.Nodes.Skip(1)).All(p => p.First.Row < p.Second.Row || (p.First.Row == p.Second.Row && p.First.Col <= p.Second.Col)));
    }

    [Fact]
    public void Tolerant_parsing_ignores_unknown_and_missing_fields()
    {
        const string json = """
        {"profile":{"characterId":"x","characterName":"<b>A&amp;B</b>","characterLevel":"44","combatPower":"1,234","pcId":30,
                    "raceId":2,"futureField":{"nested":[1,2,3]}},
         "stat":null, "somethingNew":[{"a":1}]}
        """;
        var info = ArmoryParser.ParseCharacterInfo(json, ArmoryRegion.Europe);
        Assert.Equal("A&B", info.Profile.Name);
        Assert.Equal(44, info.Profile.Level);
        Assert.Equal(1234, info.Profile.CombatPower);
        Assert.Equal(CharacterClass.Cleric, info.Profile.Class);
        Assert.Equal(Faction.Asmodian, info.Profile.Faction);
        Assert.Null(info.Profile.ItemLevel);
        Assert.Empty(info.Stats);
        Assert.Empty(info.Titles.Titles);
        Assert.Empty(info.DaevanionBoards);

        var eq = ArmoryParser.ParseEquipment("{\"equipment\":{\"equipmentList\":[{\"id\":1,\"slotPos\":99}]}}");
        Assert.Single(eq.Items);
        Assert.Equal("", eq.Items[0].Name);
        Assert.Equal(ItemGrade.Unknown, eq.Items[0].Grade);
        Assert.Null(eq.Pet);
        Assert.Empty(eq.Skills);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE html><html><body>Service maintenance</body></html>")]
    [InlineData("{not json")]
    [InlineData("{\"unexpected\":true}")]
    public void Garbage_is_reported_as_endpoint_changed(string payload)
    {
        var ex = Assert.Throws<ArmoryException>(() => ArmoryParser.ParseCharacterInfo(payload, ArmoryRegion.Europe));
        Assert.Equal(ArmoryErrorKind.EndpointChanged, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData(5, CharacterClass.Gladiator)]
    [InlineData(12, CharacterClass.Templar)]
    [InlineData(16, CharacterClass.Ranger)]
    [InlineData(20, CharacterClass.Assassin)]
    [InlineData(22, CharacterClass.Elementalist)]
    [InlineData(26, CharacterClass.Sorcerer)]
    [InlineData(30, CharacterClass.Cleric)]
    [InlineData(34, CharacterClass.Chanter)]
    [InlineData(0, CharacterClass.Unknown)]
    [InlineData(99, CharacterClass.Unknown)]
    public void PcId_maps_to_class(int pcId, CharacterClass expected) => Assert.Equal(expected, ArmorySlots.ClassFromPcId(pcId));

    [Theory]
    [InlineData("MainHand", 1, "Main Hand")]
    [InlineData("SubHand", 2, "Off Hand")]
    [InlineData("Earring1", 11, "Earring 1")]
    [InlineData("Arcana3", 43, "Arcana 3")]
    [InlineData(null, 7, "Slot 7")]
    public void Slot_names_are_readable(string? name, int pos, string expected) =>
        Assert.Equal(expected, ArmorySlots.DisplaySlotName(name, pos));

    [Theory]
    [InlineData(999, "999")]
    [InlineData(35181, "35.2K")]
    [InlineData(292421, "292K")]
    [InlineData(4_560_000, "4.56M")]
    public void Abbreviation(long v, string expected) => Assert.Equal(expected, ArmoryText.Abbreviate(v));

    [Fact]
    public void Abbreviation_of_unknown_is_dash() => Assert.Equal("—", ArmoryText.Abbreviate(null));
}
