using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Dps.Contracts;

namespace Aion2Dps.Storage;

/// <summary>
/// The stable on-disk encoding of an <see cref="EncounterRecord"/>: System.Text.Json (camelCase property names,
/// plain enums as their names, <c>[Flags]</c> enums as numbers so 30k-hit fights stay compact, NaN/Infinity allowed)
/// wrapped in gzip. Changing these options changes the stored format, so keep them stable.
/// </summary>
public static class EncounterRecordSerializer
{
    /// <summary>The JSON options used for every stored record. Do not mutate.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            IncludeFields = false,
            WriteIndented = false,
        };
        options.Converters.Add(new NonFlagsEnumAsStringConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Plain JSON (uncompressed) — handy for export, diffing and tests.</summary>
    public static string ToJson(EncounterRecord record) => JsonSerializer.Serialize(record, Options);

    public static EncounterRecord? FromJson(string json) => JsonSerializer.Deserialize<EncounterRecord>(json, Options);

    /// <summary>gzip(JSON) bytes as stored in the <c>fights.record</c> column.</summary>
    public static byte[] Compress(EncounterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, record, Options);
        }
        return buffer.ToArray();
    }

    /// <summary>Inverse of <see cref="Compress"/>. Throws on corrupt input.</summary>
    public static EncounterRecord? Decompress(ReadOnlySpan<byte> blob) => Decompress(new MemoryStream(blob.ToArray(), writable: false));

    public static EncounterRecord? Decompress(Stream blob)
    {
        using var gzip = new GZipStream(blob, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<EncounterRecord>(gzip, Options);
    }

    /// <summary>Writes non-[Flags] enums as their names (unknown values fall back to numbers); [Flags] enums use the default numeric form.</summary>
    private sealed class NonFlagsEnumAsStringConverter : JsonConverterFactory
    {
        private readonly JsonStringEnumConverter _inner = new(namingPolicy: null, allowIntegerValues: true);

        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsEnum && !typeToConvert.IsDefined(typeof(FlagsAttribute), inherit: false);

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            _inner.CreateConverter(typeToConvert, options);
    }
}
