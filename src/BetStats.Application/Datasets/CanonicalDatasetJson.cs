using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetStats.Application.Datasets;

public static class CanonicalDatasetJson
{
    private sealed class UtcConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTime.ParseExact(reader.GetString()!, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind != DateTimeKind.Utc || value.Ticks % 10 != 0) throw new ArgumentException("UTC microseconds required.");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        }
    }
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter(), new UtcConverter() } };
    public static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, JsonSerializer.SerializeToElement(value, Options));
        return stream.ToArray();
    }
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Write(writer, p.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var v in value.EnumerateArray()) Write(writer, v); writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()!.Normalize(NormalizationForm.FormC)); break;
            default: value.WriteTo(writer); break;
        }
    }
    public static T Deserialize<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("Missing artifact.");
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Fingerprint<T>(T value) => Hash(Serialize(value));
    public static DatasetDefinition Normalize(DatasetDefinition definition) => definition with { Targets = definition.Targets.OrderBy(t => t.DateObservationId).ToArray() };
}
