using System.Diagnostics;
using Aion2Dps.Contracts;
using Aion2Dps.GameData;

namespace Aion2Dps.GameData.Tests;

public sealed class StoreFixture
{
    public GameDataStore Store { get; } = GameDataStore.LoadDefault();
}

public class GameDataStoreTests : IClassFixture<StoreFixture>
{
    private readonly GameDataStore _store;

    public GameDataStoreTests(StoreFixture fixture)
    {
        _store = fixture.Store;
        _store.Language = GameLanguage.English;
    }

    private static GameDataStore NewStore(GameLanguage l = GameLanguage.English) => GameDataStore.LoadDefault(l);

    // ------------------------------------------------------------ skills

    [Theory]
    [InlineData(16040000u, "Combustion")]
    [InlineData(11020030u, "Keen Strike")]
    [InlineData(11020000u, "Keen Strike")]
    [InlineData(18120000u, "Recuperation")]
    [InlineData(SkillIds.Dodge, "Dodge")]
    public void English_skill_names(uint id, string expected) => Assert.Equal(expected, _store.GetSkillName(id));

    [Theory]
    [InlineData(GameLanguage.Korean)]
    [InlineData(GameLanguage.ChineseSimplified)]
    [InlineData(GameLanguage.ChineseTraditional)]
    public void Localized_skill_names_differ_from_english(GameLanguage language)
    {
        var store = NewStore(language);
        foreach (uint id in new uint[] { 16040000, 11020030 })
        {
            string name = store.GetSkillName(id);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.NotEqual(_store.GetSkillName(id), name);
        }
    }

    [Fact]
    public void Chinese_variants_differ()
    {
        Assert.Equal("火焰烧毁", NewStore(GameLanguage.ChineseSimplified).GetSkillName(16040000));
        Assert.Equal("火焰燒毀", NewStore(GameLanguage.ChineseTraditional).GetSkillName(16040000));
        Assert.Equal("화염 전소", NewStore(GameLanguage.Korean).GetSkillName(16040000));
    }

    [Fact]
    public void Unknown_variant_falls_back_to_base_then_tens()
    {
        // 11020099 is not in the table, base 11020000 is.
        Assert.Equal("Keen Strike", _store.GetSkillName(11020099));
        Assert.Equal(11020000u, _store.GetSkillGroupKey(11020099));
    }

    [Fact]
    public void Readable_fallbacks_for_unknown_ids()
    {
        Assert.Equal("Skill 19999999", _store.GetSkillName(19_999_999));
        Assert.Equal("Monster attack", _store.GetSkillName(9_999_998));
        Assert.Equal("Theostone", _store.GetSkillName(30_999_991));
        Assert.Equal("Spirit attack", _store.GetSkillName(199_998));
        Assert.Equal("Skill 0", _store.GetSkillName(0));
        Assert.Equal("Skill 4294967295", _store.GetSkillName(uint.MaxValue));
        Assert.Equal("스킬 19999999", NewStore(GameLanguage.Korean).GetSkillName(19_999_999));
    }

    [Fact]
    public void Theostone_raw_ids_are_normalized()
    {
        // Raw 3000017 → 30000171 "Theostone: Zikel's Vestige".
        Assert.Equal(_store.GetSkillName(30_000_171), _store.GetSkillName(3_000_017));
        Assert.StartsWith("Theostone", _store.GetSkillName(3_000_017));
        Assert.Equal(30_000_171u, _store.GetSkillGroupKey(3_000_017));
    }

    [Fact]
    public void Spirit_and_link_names()
    {
        Assert.Equal("Fire Spirit: Basic Attack", _store.GetSkillName(100011));
        Assert.Equal("Water Spirit: Basic Attack", _store.GetSkillName(16990002));
        Assert.Equal("Spirit link", _store.GetSkillName(16_999_998));
    }

    [Fact]
    public void Group_keys_follow_spec()
    {
        Assert.Equal(11020000u, _store.GetSkillGroupKey(11020030)); // same name as base
        Assert.Equal(17060000u, _store.GetSkillGroupKey(17060010)); // "Bolt" == base "Bolt"
        Assert.Equal(17060001u, _store.GetSkillGroupKey(17060001)); // "Bolt Level 1" has its own name
        Assert.Equal(17400027u, _store.GetSkillGroupKey(17400027)); // "Earth's Blessing" ≠ "Earth Punishment"
        Assert.Equal(17400000u, _store.GetSkillGroupKey(17400010));
        Assert.Equal(SkillIds.Dodge, _store.GetSkillGroupKey(SkillIds.Dodge));
        Assert.Equal(9_999_998u, _store.GetSkillGroupKey(9_999_998)); // NPC ids are never merged
    }

    [Fact]
    public void Group_keys_are_language_independent()
    {
        var ko = NewStore(GameLanguage.Korean);
        foreach (uint id in new uint[] { 11020030, 17060001, 17060010, 17400027, 16040000 })
            Assert.Equal(_store.GetSkillGroupKey(id), ko.GetSkillGroupKey(id));
    }

    [Fact]
    public void Skill_class_and_icon()
    {
        Assert.Equal(CharacterClass.Elementalist, _store.GetSkillClass(16040000));
        Assert.Equal(CharacterClass.Brawler, _store.GetSkillClass(19010000));
        Assert.Equal(CharacterClass.Unknown, _store.GetSkillClass(2300104));
        Assert.NotNull(_store.GetSkillIconKey(11020030));
        Assert.StartsWith("ICON_", _store.GetSkillIconKey(11020030));
        Assert.Null(_store.GetSkillIconKey(99_990_000));
    }

    // ------------------------------------------------------------ classification

    [Theory]
    [InlineData(18120000u)] // Recuperation (healing_skill_ids.json)
    [InlineData(18120031u)] // Recuperation variant (base match)
    [InlineData(17100000u)] // Healing Light
    [InlineData(17120001u)] // Radiant Recovery MAX
    [InlineData(17090000u)] // Light of Regeneration (curated)
    [InlineData(17320000u)] // Blessing of Regeneration (curated)
    [InlineData(11730007u)] // Blood Absorption: Gladiator self-heal (curated, see heal_skill_families.json)
    [InlineData(11120000u)] // Blood Absorption
    [InlineData(13790000u)] // Revitalization Contract
    [InlineData(10000001u)] // HP Absorption effect
    [InlineData(17400027u)] // Earth's Blessing (exact heal variant inside a damage family)
    [InlineData(1220690u)]  // NPC "Heal" (exact only)
    public void Heal_skills(uint id) => Assert.True(_store.IsHealSkill(id), $"{id} {_store.GetSkillName(id)}");

    [Theory]
    [InlineData(16040000u)] // Combustion
    [InlineData(11020030u)] // Keen Strike
    [InlineData(17060010u)] // Bolt
    [InlineData(17400010u)] // Earth Punishment (damage family containing a heal variant)
    [InlineData(11340000u)] // Lifestealing Blade: a damage skill
    [InlineData(17760000u)] // Heal Block: a debuff
    [InlineData(SkillIds.Dodge)]
    [InlineData(1220691u)]  // neighbour of an NPC heal: exact match only
    [InlineData(0u)]
    public void Non_heal_skills(uint id) => Assert.False(_store.IsHealSkill(id), $"{id} {_store.GetSkillName(id)}");

    [Fact]
    public void Dot_skills()
    {
        Assert.True(_store.IsDotSkill(11410000));  // listed
        Assert.True(_store.IsDotSkill(11410007));  // listed variant
        Assert.True(_store.IsDotSkill(11419999));  // base match
        Assert.False(_store.IsDotSkill(11020030));
        Assert.False(_store.IsDotSkill(0x7FFF_FFFF));
    }

    // ------------------------------------------------------------ NPCs and maps

    [Theory]
    [InlineData(2300104u, "Enhanced Harcon")]
    [InlineData(2300171u, "Ultimate Berk")]
    public void Bosses(uint code, string name)
    {
        var npc = _store.GetNpc(code);
        Assert.NotNull(npc);
        Assert.True(npc.IsBoss);
        Assert.False(npc.IsDummy);
        Assert.Equal(name, npc.Name);
        Assert.Equal(600001u, npc.DungeonId);
        Assert.Equal(name, _store.GetNpcName(code));
    }

    [Theory]
    [InlineData(2000000u)]
    [InlineData(2400032u)]
    [InlineData(2400035u)]
    [InlineData(2090773u)]
    [InlineData(2300229u)]
    public void Dummies(uint code)
    {
        var npc = _store.GetNpc(code);
        Assert.NotNull(npc);
        Assert.True(npc.IsDummy);
    }

    [Fact]
    public void Npc_localized_and_unknown()
    {
        var ko = NewStore(GameLanguage.Korean);
        Assert.Equal("강화된 하르콘", ko.GetNpcName(2300104));
        Assert.True(ko.GetNpc(2300104)!.IsBoss);
        Assert.Null(_store.GetNpc(1));
        Assert.Equal("NPC 1", _store.GetNpcName(1));
        Assert.Equal("NPC 2999999", _store.GetNpcName(2999999));
        Assert.False(_store.GetNpc(2000002)!.IsBoss);
    }

    [Fact]
    public void Maps()
    {
        Assert.Equal("Fire Temple", _store.GetMapName(600021));
        Assert.Equal("Altgard", _store.GetMapName(1110)); // open-world name from field_boss_maps.json
        Assert.Null(_store.GetMapName(123456789));
        Assert.True(_store.IsInstanceMap(600021));
        Assert.True(_store.IsInstanceMap(699999));
        Assert.False(_store.IsInstanceMap(700000));
        Assert.False(_store.IsInstanceMap(1110));
        Assert.True(_store.IsOpenWorldMap(1110));
        Assert.False(_store.IsOpenWorldMap(600021));
        var ko = NewStore(GameLanguage.Korean);
        Assert.NotEqual("Fire Temple", ko.GetMapName(600021));
        Assert.False(string.IsNullOrWhiteSpace(ko.GetMapName(600021)));
    }

    // ------------------------------------------------------------ servers and classes

    [Fact]
    public void Servers()
    {
        Assert.Equal("Siel", _store.GetServerName(1001));
        Assert.Equal("Nezekan", _store.GetServerName(1002));
        Assert.Null(_store.GetServerName(0));
        Assert.Null(_store.GetServerName(65535));
        Assert.Equal("시엘", NewStore(GameLanguage.Korean).GetServerName(1001));
        Assert.Equal("希埃爾", NewStore(GameLanguage.ChineseTraditional).GetServerName(1001));
        // No zh-Hans server names in the data: falls back to Traditional.
        Assert.Equal("希埃爾", NewStore(GameLanguage.ChineseSimplified).GetServerName(1001));
    }

    [Fact]
    public void Class_names()
    {
        Assert.Equal("Gladiator", _store.GetClassName(CharacterClass.Gladiator));
        Assert.Equal("Brawler", _store.GetClassName(CharacterClass.Brawler));
        Assert.Equal("Unknown", _store.GetClassName(CharacterClass.Unknown));
        Assert.Equal("Unknown", _store.GetClassName((CharacterClass)99));
        Assert.Equal("검성", NewStore(GameLanguage.Korean).GetClassName(CharacterClass.Gladiator));
        Assert.Equal("剑圣", NewStore(GameLanguage.ChineseSimplified).GetClassName(CharacterClass.Gladiator));
        Assert.Equal("劍聖", NewStore(GameLanguage.ChineseTraditional).GetClassName(CharacterClass.Gladiator));
        foreach (var c in ClassInfo.All)
            foreach (var l in Enum.GetValues<GameLanguage>())
                Assert.False(string.IsNullOrWhiteSpace(LocalizedText.ClassName(c, l)));
    }

    // ------------------------------------------------------------ language switching

    [Fact]
    public void Language_switch_raises_event_and_changes_names()
    {
        var store = NewStore();
        int raised = 0;
        store.LanguageChanged += () => raised++;

        store.Language = GameLanguage.Korean;
        Assert.Equal(1, raised);
        Assert.Equal(GameLanguage.Korean, store.Language);
        Assert.Equal("화염 전소", store.GetSkillName(16040000));
        Assert.Equal("검성", store.GetClassName(CharacterClass.Gladiator));

        store.Language = GameLanguage.Korean; // no change → no event
        Assert.Equal(1, raised);

        store.Language = GameLanguage.English;
        Assert.Equal(2, raised);
        Assert.Equal("Combustion", store.GetSkillName(16040000));
    }

    [Fact]
    public void Throwing_handler_does_not_break_setter()
    {
        var store = NewStore();
        store.LanguageChanged += () => throw new InvalidOperationException("boom");
        store.Language = GameLanguage.ChineseTraditional;
        Assert.Equal("火焰燒毀", store.GetSkillName(16040000));
    }

    [Fact]
    public void Missing_data_directory_never_throws()
    {
        var store = new GameDataStore(Path.Combine(Path.GetTempPath(), "aion2dps-no-such-dir-" + Guid.NewGuid()));
        Assert.Equal("Skill 16040000", store.GetSkillName(16040000));
        Assert.Equal("NPC 2300104", store.GetNpcName(2300104));
        Assert.True(store.GetNpc(2400032)!.IsDummy);
        Assert.Null(store.GetMapName(600021));
        Assert.Null(store.GetServerName(1001));
        Assert.False(store.IsHealSkill(18120000));
        Assert.Equal("Gladiator", store.GetClassName(CharacterClass.Gladiator));
        store.Language = GameLanguage.Korean;
        Assert.Equal("스킬 16040000", store.GetSkillName(16040000));
    }

    [Fact]
    public async Task Concurrent_reads_while_switching_language()
    {
        var store = NewStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var langs = Enum.GetValues<GameLanguage>();
        int errors = 0;

        var readers = Enumerable.Range(0, 6).Select(t => Task.Run(() =>
        {
            uint i = (uint)t;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    i++;
                    if (string.IsNullOrEmpty(store.GetSkillName(16040000 + i % 50))) Interlocked.Increment(ref errors);
                    store.GetSkillGroupKey(11020000 + i % 100);
                    store.IsHealSkill(18120000 + i % 10);
                    store.IsDotSkill(11410000 + i % 10);
                    if (string.IsNullOrEmpty(store.GetNpcName(2300100 + i % 100))) Interlocked.Increment(ref errors);
                    store.GetMapName(600000 + i % 100);
                    store.GetServerName((ushort)(1000 + i % 30));
                    store.GetClassName(ClassInfo.All[(int)(i % 9)]);
                }
                catch { Interlocked.Increment(ref errors); }
            }
        })).ToList();

        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!cts.IsCancellationRequested) store.Language = langs[n++ % langs.Length];
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, errors);
    }

    [Fact]
    public void Loads_all_languages_quickly()
    {
        var sw = Stopwatch.StartNew();
        var store = NewStore();
        store.PreloadAllLanguages();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"took {sw.ElapsedMilliseconds} ms");
        foreach (var l in Enum.GetValues<GameLanguage>())
        {
            store.Language = l;
            Assert.False(string.IsNullOrWhiteSpace(store.GetSkillName(11020030)));
        }
    }
}
