using System.Text.Json;
using System.Text.Json.Nodes;

namespace OsrsFlipping.Core;

public static class JsonExtensions
{
    public static string S(this JsonNode? n) => n.StringValue();
    public static string StringValue(this JsonNode? n, string fallback = "") => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static long L(this JsonNode? n, long fallback = 0) => n.LongValue(fallback);
    public static double D(this JsonNode? n, double fallback = 0) => n.DoubleValue(fallback);
    public static bool B(this JsonNode? n, bool fallback = false) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public static string S(this JsonNode? n, string key, string fallback = "") => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    public static long L(this JsonNode? n, string key, long fallback = 0) => n?[key].LongValue(fallback) ?? fallback;
    public static int I(this JsonNode? n, string key, int fallback = 0)
    { var value = n.L(key, fallback); return value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback; }
    public static double D(this JsonNode? n, string key, double fallback = 0) => n?[key].DoubleValue(fallback) ?? fallback;
    public static bool B(this JsonNode? n, string key, bool fallback = false) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public static JsonObject Obj(this JsonNode? n, string key) => n?[key] as JsonObject ?? new JsonObject();
    public static JsonArray Arr(this JsonNode? n, string key) => n?[key] as JsonArray ?? new JsonArray();
    public static IEnumerable<JsonObject> Objects(this JsonNode? n, string key) => n.Arr(key).OfType<JsonObject>();
    public static JsonObject Copy(this JsonObject n) => (JsonObject)n.DeepClone();
    public static long LongValue(this JsonNode? n, long fallback = 0)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out l)) return l;
        return fallback;
    }
    public static double DoubleValue(this JsonNode? n, double fallback = 0)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : fallback;
        var l = n.LongValue(long.MinValue); return l == long.MinValue ? fallback : l;
    }
    public static bool IsInteger(this JsonNode? n) => n.LongValue(long.MinValue) != long.MinValue;
}

public static class J
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static JsonNode? Node(object? value) => value is JsonNode n ? n.DeepClone() : JsonSerializer.SerializeToNode(value, Options);
    public static JsonObject O(params (string Key, object? Value)[] pairs)
    {
        var result = new JsonObject(); foreach (var (key, value) in pairs) result[key] = Node(value); return result;
    }
    public static JsonArray A(IEnumerable<JsonNode?> nodes) => new(nodes.Select(n => n?.DeepClone()).ToArray());
    public static JsonArray A(params object?[] values) => new(values.Select(Node).ToArray());
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray(); if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }
}
