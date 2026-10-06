namespace Aion2Dps.App.Controls;

/// <summary>Original vector glyphs (16×16 design box) for the class emblems and the target crest.</summary>
public static class Glyphs
{
    public readonly record struct Glyph(Geometry? Fill, Geometry? Stroke);

    private static readonly Dictionary<CharacterClass, Glyph> ClassGlyphs = new()
    {
        // Greatsword
        [CharacterClass.Gladiator] = G("M8,0.8 L9.6,3 L9.6,9.6 L12.4,9.6 L12.4,11.3 L9.6,11.3 L9.6,13 L10.4,15.2 L5.6,15.2 L6.4,13 L6.4,11.3 L3.6,11.3 L3.6,9.6 L6.4,9.6 L6.4,3 Z"),
        // Kite shield with a boss
        [CharacterClass.Templar] = G("F0 M8,0.8 L14.2,3.3 C14.2,9 11.6,12.9 8,15.2 C4.4,12.9 1.8,9 1.8,3.3 Z M8,4.2 L10.6,5.4 C10.6,8.3 9.6,10.4 8,11.8 C6.4,10.4 5.4,8.3 5.4,5.4 Z"),
        // Arrow in flight
        [CharacterClass.Ranger] = G("M1.6,13.3 L9.9,5 L11,6.1 L2.7,14.4 Z M8.6,3.1 L14.6,1.4 L12.9,7.4 Z M1.2,11.6 L3.6,11.6 L2.4,12.8 Z M4.4,14.8 L4.4,12.4 L3.2,13.6 Z"),
        // Dagger
        [CharacterClass.Assassin] = G("M8,0.6 L10.2,8 L8,10.6 L5.8,8 Z M4.2,10.8 L11.8,10.8 L11.8,12.2 L4.2,12.2 Z M7.1,12.2 L8.9,12.2 L8.9,15.4 L7.1,15.4 Z"),
        // Spirit orb with an orbit
        [CharacterClass.Elementalist] = G("M5,8 A3,3 0 1 1 11,8 A3,3 0 1 1 5,8 Z",
            "M1.2,8.6 A6.8,2.6 0 1 0 14.8,8.6 A6.8,2.6 0 1 0 1.2,8.6"),
        // Flame
        [CharacterClass.Sorcerer] = G("M8,0.6 C9.2,4 12.8,5.6 12.8,9.9 C12.8,13 10.6,15.3 8,15.3 C5.4,15.3 3.2,13 3.2,10.1 C3.2,7.9 4.6,6.6 5.4,4.9 C6.2,6.5 6.8,7.4 7.7,7.8 C7.3,5.2 7.3,2.9 8,0.6 Z"),
        // Cross
        [CharacterClass.Cleric] = G("M6.4,1.2 L9.6,1.2 L9.6,5.2 L13.8,5.2 L13.8,8.4 L9.6,8.4 L9.6,14.8 L6.4,14.8 L6.4,8.4 L2.2,8.4 L2.2,5.2 L6.4,5.2 Z"),
        // Eight-point mantra star
        [CharacterClass.Chanter] = G("M8,0.6 L9.3,5.6 L13.2,2.8 L10.4,6.7 L15.4,8 L10.4,9.3 L13.2,13.2 L9.3,10.4 L8,15.4 L6.7,10.4 L2.8,13.2 L5.6,9.3 L0.6,8 L5.6,6.7 L2.8,2.8 L6.7,5.6 Z"),
        // Gauntlet / fist
        [CharacterClass.Brawler] = G("M2.8,6.2 C2.8,5 3.8,4 5,4 L11,4 C12.2,4 13.2,5 13.2,6.2 L13.2,10 C13.2,12.9 10.9,15.2 8,15.2 L7.2,15.2 C4.8,15.2 2.8,13.2 2.8,10.8 Z M4,1.4 L12,1.4 L12,2.9 L4,2.9 Z"),
        [CharacterClass.Unknown] = G("F0 M2.5,8 A5.5,5.5 0 1 1 13.5,8 A5.5,5.5 0 1 1 2.5,8 Z M5.5,8 A2.5,2.5 0 1 0 10.5,8 A2.5,2.5 0 1 0 5.5,8 Z"),
    };

    /// <summary>Horned crest used for bosses in the target emblem.</summary>
    public static readonly Glyph Boss = new(Cut(
        // Curved horns + skull with jaw
        "M5.4,6.2 C3.6,5.2 2,3.6 1.4,0.8 C3.4,2.2 5.4,3.4 7,4.6 Z M10.6,6.2 C12.4,5.2 14,3.6 14.6,0.8 C12.6,2.2 10.6,3.4 9,4.6 Z " +
        "M8,4.2 C11.1,4.2 12.8,6.3 12.8,8.9 C12.8,10.5 12,11.6 10.9,12.2 L10.9,14.8 L5.1,14.8 L5.1,12.2 C4,11.6 3.2,10.5 3.2,8.9 C3.2,6.3 4.9,4.2 8,4.2 Z",
        // Eye sockets, nose, teeth gaps
        "M5,8.9 C5,8 5.8,7.6 6.8,8 C7.4,8.3 7.3,9.7 6.3,10.1 C5.6,10.4 5,9.9 5,8.9 Z M11,8.9 C11,8 10.2,7.6 9.2,8 C8.6,8.3 8.7,9.7 9.7,10.1 C10.4,10.4 11,9.9 11,8.9 Z " +
        "M8,10.5 L8.8,11.9 L7.2,11.9 Z M6.7,13.1 L7.4,13.1 L7.4,14.8 L6.7,14.8 Z M8.6,13.1 L9.3,13.1 L9.3,14.8 L8.6,14.8 Z"), null);

    /// <summary>Concentric target for training dummies / generic targets.</summary>
    public static readonly Glyph Dummy = G("F0 M1.5,8 A6.5,6.5 0 1 1 14.5,8 A6.5,6.5 0 1 1 1.5,8 Z M3.7,8 A4.3,4.3 0 1 0 12.3,8 A4.3,4.3 0 1 0 3.7,8 Z M5.8,8 A2.2,2.2 0 1 1 10.2,8 A2.2,2.2 0 1 1 5.8,8 Z");

    /// <summary>Crossed blades for PvP.</summary>
    public static readonly Glyph Pvp = G("M2,1.2 L10.8,10 L10,10.8 L1.2,2 Z M14,1.2 L14.8,2 L6,10.8 L5.2,10 Z M9.4,11.6 L11.6,9.4 L14.8,12.6 L12.6,14.8 Z M1.2,12.6 L4.4,9.4 L6.6,11.6 L3.4,14.8 Z");

    /// <summary>Hourglass for idle/waiting states.</summary>
    public static readonly Glyph Waiting = G("M3.5,1.5 L12.5,1.5 L12.5,3 C12.5,5.5 10,7 9,8 C10,9 12.5,10.5 12.5,13 L12.5,14.5 L3.5,14.5 L3.5,13 C3.5,10.5 6,9 7,8 C6,7 3.5,5.5 3.5,3 Z");

    public static Glyph ForClass(CharacterClass c) => ClassGlyphs.TryGetValue(c, out var g) ? g : ClassGlyphs[CharacterClass.Unknown];

    /// <summary>Union of <paramref name="shapes"/> minus <paramref name="holes"/>.</summary>
    private static Geometry Cut(string shapes, string holes)
    {
        var g = new CombinedGeometry(GeometryCombineMode.Exclude, Geometry.Parse(shapes), Geometry.Parse(holes)).GetFlattenedPathGeometry();
        g.Freeze();
        return g;
    }

    private static Glyph G(string fill, string? stroke = null)
    {
        var f = Geometry.Parse(fill);
        f.Freeze();
        Geometry? s = null;
        if (stroke is not null)
        {
            s = Geometry.Parse(stroke);
            s.Freeze();
        }
        return new Glyph(f, s);
    }
}
