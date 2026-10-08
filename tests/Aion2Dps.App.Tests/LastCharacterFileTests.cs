using Aion2Dps.App.Integration;
using Aion2Dps.Combat;
using Aion2Dps.Contracts;
using Aion2Dps.App.Demo;

namespace Aion2Dps.App.Tests;

public class LastCharacterFileTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "a2d-lastchar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Save_and_load_round_trip_and_bad_files_mean_no_hint()
    {
        string dir = TempDir();
        try
        {
            Assert.Null(LastCharacterFile.Load(dir));
            LastCharacterFile.Save(dir, new KnownCharacter("Hisoka", CharacterClass.Assassin, 2320));
            Assert.Equal(new KnownCharacter("Hisoka", CharacterClass.Assassin, 2320), LastCharacterFile.Load(dir));
            File.WriteAllText(Path.Combine(dir, LastCharacterFile.FileName), "{ not json");
            Assert.Null(LastCharacterFile.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Attached_engine_remembers_the_character_named_by_its_identity_record()
    {
        string dir = TempDir();
        try
        {
            LastCharacterFile.Save(dir, new KnownCharacter("Old", CharacterClass.Cleric, 1304));
            var engine = new CombatEngine(new FakeGameData());
            LastCharacterFile.Attach(engine, dir);
            Assert.Equal("Old", engine.Options.KnownLocalCharacter!.Name); // loaded at start-up
            engine.OnEvent(new SelfInfoEvent
            {
                Time = DateTime.UtcNow, Entity = 42, Name = "Hisoka", ServerId = 2320, ClassCode = 18, Class = CharacterClass.Assassin, Level = 39,
            });
            Assert.Equal(new KnownCharacter("Hisoka", CharacterClass.Assassin, 2320), LastCharacterFile.Load(dir));
            Assert.Equal("Hisoka", engine.Options.KnownLocalCharacter!.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
