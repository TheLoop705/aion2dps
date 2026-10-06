using System.Globalization;
using System.Text.Json;

namespace Aion2Dps.Armory;

/// <summary>Tolerant accessors over <see cref="JsonElement"/>: missing / null / wrong-typed members give null instead of
/// throwing, numbers sent as strings (and the reverse) are accepted, member names match case-insensitively as a fallback.</summary>
internal static class JsonRead
{
    public static JsonElement? Prop(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : v;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value;
        return null;
    }

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Arr(this JsonElement? e, string name) => e is { } x ? x.Arr(name) : [];

    public static string? Str(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string? Str(this JsonElement? e, string name) => e is { } x ? x.Str(name) : null;

    public static long? Long(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d) && !double.IsNaN(d) && Math.Abs(d) < 9e18) return (long)Math.Round(d);
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (long.TryParse(s, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) < 9e18) return (long)Math.Round(d);
        }
        if (v.ValueKind == JsonValueKind.True) return 1;
        if (v.ValueKind == JsonValueKind.False) return 0;
        return null;
    }

    public static long? Long(this JsonElement? e, string name) => e is { } x ? x.Long(name) : null;

    public static int? Int(this JsonElement e, string name)
    {
        var l = e.Long(name);
        return l is null ? null : (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue);
    }

    public static int? Int(this JsonElement? e, string name) => e is { } x ? x.Int(name) : null;

    public static bool? Bool(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l != 0 : null,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b
                : long.TryParse(v.GetString(), out var n) ? n != 0 : null,
            _ => null,
        };
    }

    /// <summary>Array of strings, or of objects with a "desc" member (both shapes are used by the site).</summary>
    public static IReadOnlyList<string> Strings(this JsonElement e, string name, string objectMember = "desc")
    {
        var list = new List<string>();
        foreach (var x in e.Arr(name))
        {
            var s = x.ValueKind switch
            {
                JsonValueKind.String => x.GetString(),
                JsonValueKind.Object => x.Str(objectMember),
                JsonValueKind.Number => x.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) list.Add(ArmoryText.Clean(s));
        }
        return list;
    }
}
