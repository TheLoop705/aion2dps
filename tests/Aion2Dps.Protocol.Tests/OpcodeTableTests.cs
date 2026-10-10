using Aion2Dps.Contracts;

namespace Aion2Dps.Protocol.Tests;

public class OpcodeTableTests
{
    [Fact]
    public void Defaults_come_from_contracts()
    {
        var t = OpcodeTable.Default;
        Assert.Equal(Opcodes.Damage, t.Damage);
        Assert.Equal(Opcodes.Spawn, t.Spawn);
        Assert.Equal(Opcodes.FieldBossList, t.FieldBossList);
        Assert.Equal(24, t.ToDictionary().Count);
        Assert.Equal(OpcodeTable.Keys.Count, t.ToDictionary().Count);
        Assert.True(t.IsKnown(Opcodes.Damage));
        Assert.True(t.IsKnown(Opcodes.Bundle));
        Assert.True(t.IsKnown(0x26E2)); // census-only opcode
        Assert.False(t.IsKnown(0x1234));
    }

    [Fact]
    public void Shipped_opcodes_json_loads_and_matches_defaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "protocol", "opcodes.json");
        Assert.True(File.Exists(path), path);
        var t = OpcodeTable.LoadOrDefault(path);
        Assert.Equal(path, t.Source);
        foreach (var (key, value) in OpcodeTable.Default.ToDictionary())
            Assert.Equal(value, t.ToDictionary()[key]);
        Assert.Equal(path, OpcodeTable.LoadOrDefault().Source);
    }

    [Fact]
    public void Json_overrides_keys_and_keeps_the_rest()
    {
        Assert.True(OpcodeTable.TryParse("""{ "damage": "04 39", "spawn": "0x4236", "kill": 1165, "bogus": "11 11", "syncOpcodes": ["12 34"] }""", out var t));
        Assert.Equal(0x0439, t!.Damage);
        Assert.Equal(0x4236, t.Spawn);
        Assert.Equal(1165, t.Kill);
        Assert.Equal(Opcodes.DotTick, t.DotTick);
        Assert.True(t.IsKnown(0x1234));
        Assert.True(t.IsKnown(0x0439));
    }

    [Fact]
    public void Invalid_values_keep_defaults_and_bad_files_fall_back()
    {
        Assert.True(OpcodeTable.TryParse("""{ "damage": "zz", "dotTick": [1] }""", out var t));
        Assert.Equal(Opcodes.Damage, t!.Damage);
        Assert.Equal(Opcodes.DotTick, t.DotTick);
        Assert.False(OpcodeTable.TryParse("{ not json", out _));
        Assert.False(OpcodeTable.TryParse("[1,2]", out _));
        Assert.Same(OpcodeTable.Default, OpcodeTable.LoadOrDefault(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json")));

        var bad = Path.Combine(Path.GetTempPath(), "aion2dps-bad-opcodes-" + Guid.NewGuid() + ".json");
        File.WriteAllText(bad, "{ broken");
        try { Assert.Same(OpcodeTable.Default, OpcodeTable.LoadOrDefault(bad)); }
        finally { File.Delete(bad); }
    }

    /// <summary>A patch that moves an opcode is fixed by data: the decoder dispatches through the table.</summary>
    [Fact]
    public void Decoder_dispatches_by_the_table()
    {
        var file = Path.Combine(Path.GetTempPath(), "aion2dps-opcodes-" + Guid.NewGuid() + ".json");
        File.WriteAllText(file, """{ "damage": "04 39" }""");
        try
        {
            var table = OpcodeTable.LoadOrDefault(file);
            var events = new EventCollector();
            var dec = new PacketDecoder(events, table);
            var body = W.H("848403 06 00 be08 fe26a800 4f 02 000002 433baf41 01000000 ba4f fd02 0100");
            dec.OnFrame(new Frame(W.T0, 0x0438, body, 0)); // old opcode: no longer damage
            Assert.Empty(events.Events);
            dec.OnFrame(new Frame(W.T0, 0x0439, body, 0));
            Assert.Equal(381, Assert.IsType<DamageEvent>(Assert.Single(events.Events)).Amount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void With_replaces_opcodes()
    {
        var t = OpcodeTable.Default.With(new Dictionary<string, ushort> { ["heartbeat"] = 0x0136 });
        Assert.Equal(0x0136, t.Heartbeat);
        Assert.Equal(Opcodes.Damage, t.Damage);
    }
}
