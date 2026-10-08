using System.Text.Json;
using Aion2Dps.Combat;

namespace Aion2Dps.App.Integration;

/// <summary>
/// Remembers the last character the game named as the local player (its own <c>33 36</c> record), in
/// <c>last-character.json</c> next to the fight history. After a meter restart in the middle of a session (no identity
/// record until the next zone change) the engine names the inferred local player from it when the class agrees.
/// Never throws: a missing or damaged file just means no hint.
/// </summary>
public static class LastCharacterFile
{
    public const string FileName = "last-character.json";

    private sealed record Stored(string Name, string Class, ushort ServerId);

    public static KnownCharacter? Load(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 48) return null;
            var cls = Enum.TryParse<CharacterClass>(s.Class, ignoreCase: true, out var c) ? c : CharacterClass.Unknown;
            return new KnownCharacter(s.Name, cls, s.ServerId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("App", $"Could not read the remembered character: {ex.Message}");
            return null;
        }
    }

    public static void Save(string dataDirectory, KnownCharacter character)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            string path = Path.Combine(dataDirectory, FileName);
            string json = JsonSerializer.Serialize(new Stored(character.Name, character.Class.ToString(), character.ServerId));
            if (File.Exists(path) && File.ReadAllText(path) == json) return;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("App", $"Could not remember the character: {ex.Message}");
        }
    }

    /// <summary>Feeds the remembered character to the engine and keeps the file up to date.</summary>
    public static void Attach(CombatEngine engine, string dataDirectory)
    {
        engine.Options.KnownLocalCharacter ??= Load(dataDirectory);
        KnownCharacter? last = engine.Options.KnownLocalCharacter;
        engine.LocalCharacterIdentified += c =>
        {
            if (c == last) return;
            last = c;
            engine.Options.KnownLocalCharacter = c;
            Save(dataDirectory, c);
        };
    }
}
