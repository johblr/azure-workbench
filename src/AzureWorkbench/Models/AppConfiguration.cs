using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureWorkbench.Models;

/// <summary>An App Service application setting (exposed to the app as an environment variable).</summary>
public sealed record AppSetting(string Name, string Value, bool SlotSetting = false);

/// <summary>An App Service connection string. Type is the ARM ConnectionStringType name, e.g. SQLAzure or Custom.</summary>
public sealed record ConnectionStringSetting(string Name, string Value, string Type = "Custom", bool SlotSetting = false);

/// <summary>A snapshot of an app's environment configuration as returned by Azure.</summary>
public sealed record AppConfiguration(IReadOnlyList<AppSetting> Settings, IReadOnlyList<ConnectionStringSetting> ConnectionStrings)
{
    public static readonly string[] ConnectionStringTypes =
    [
        "Custom", "SQLAzure", "SQLServer", "MySql", "PostgreSQL", "DocDb", "RedisCache",
        "ServiceBus", "EventHub", "NotificationHub", "ApiHub",
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>True when both snapshots hold the same names, values, types and slot flags (order-insensitive).</summary>
    public bool SameAs(AppConfiguration other) =>
        Diff.Between(this, other).Count == 0;

    /// <summary>Validates names before saving. Returns blocking errors and non-blocking warnings.</summary>
    public (List<string> Errors, List<string> Warnings) Validate(bool linux)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        void Check<T>(IEnumerable<T> items, Func<T, string> name, string label)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var n = name(item);
                if (string.IsNullOrWhiteSpace(n)) errors.Add($"{label} names cannot be blank.");
                else if (n != n.Trim()) errors.Add($"{label} '{n}' has leading or trailing spaces.");
                else if (!seen.Add(n)) errors.Add($"Duplicate {label.ToLowerInvariant()} '{n}' (names are case-insensitive).");
                else if (n.Any(char.IsControl) || n.Contains('='))
                    errors.Add($"{label} '{n}' contains '=' or control characters.");
                else if (linux && !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-'))
                    warnings.Add($"{label} '{n}' has characters Linux apps may not receive as an environment variable.");
            }
        }
        Check(Settings, s => s.Name, "Setting");
        Check(ConnectionStrings, c => c.Name, "Connection string");
        foreach (var c in ConnectionStrings.Where(c => !ConnectionStringTypes.Contains(c.Type)))
            errors.Add($"Connection string '{c.Name}' has unknown type '{c.Type}'.");
        return (errors.Distinct().ToList(), warnings);
    }

    /// <summary>Portal-compatible "Advanced edit" JSON for app settings.</summary>
    public static string SettingsToJson(IEnumerable<AppSetting> settings) =>
        JsonSerializer.Serialize(settings.Select(s => new JsonSetting(s.Name, s.Value, null, s.SlotSetting)), JsonOptions);

    public static string ConnectionStringsToJson(IEnumerable<ConnectionStringSetting> items) =>
        JsonSerializer.Serialize(items.Select(c => new JsonSetting(c.Name, c.Value, c.Type, c.SlotSetting)), JsonOptions);

    public static List<AppSetting> SettingsFromJson(string json) =>
        ParseJson(json).Select(s => new AppSetting(s.Name ?? "", s.Value ?? "", s.SlotSetting ?? false)).ToList();

    public static List<ConnectionStringSetting> ConnectionStringsFromJson(string json) =>
        ParseJson(json).Select(s => new ConnectionStringSetting(s.Name ?? "", s.Value ?? "",
            string.IsNullOrWhiteSpace(s.Type) ? "Custom" : CanonicalType(s.Type), s.SlotSetting ?? false)).ToList();

    public static string CanonicalType(string type) =>
        ConnectionStringTypes.FirstOrDefault(t => t.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase)) ?? type.Trim();

    static List<JsonSetting> ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Advanced edit JSON must be an array of {\"name\", \"value\", \"slotSetting\"} objects.");
        var result = new List<JsonSetting>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each entry must be an object with \"name\" and \"value\".");
            string? Str(string key) => item.TryGetProperty(key, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Null => null,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                    _ => throw new ArgumentException($"\"{key}\" must be a string."),
                }
                : null;
            bool? slot = item.TryGetProperty("slotSetting", out var s)
                ? s.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False or JsonValueKind.Null => false,
                    _ => throw new ArgumentException("\"slotSetting\" must be true or false."),
                }
                : null;
            result.Add(new JsonSetting(Str("name"), Str("value"), Str("type"), slot));
        }
        return result;
    }

    sealed record JsonSetting(
        string? Name, string? Value,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
        bool? SlotSetting);
}

public enum ChangeKind { Added, Changed, Removed }

/// <summary>A single difference between two configuration snapshots. Never includes values, so it is safe to display.</summary>
public sealed record ConfigChange(ChangeKind Kind, string Section, string Name, string Detail)
{
    public override string ToString() => $"{Kind switch { ChangeKind.Added => "+", ChangeKind.Removed => "−", _ => "~" }} {Section}: {Name}{(Detail.Length > 0 ? $" ({Detail})" : "")}";
}

public static class Diff
{
    public static List<ConfigChange> Between(AppConfiguration before, AppConfiguration after)
    {
        var changes = new List<ConfigChange>();
        Compare(before.Settings, after.Settings, s => s.Name, "Setting", (a, b) => Details(
            (a.Value != b.Value, "value"), (a.SlotSetting != b.SlotSetting, "slot setting"), (a.Name != b.Name, "name casing")), changes);
        Compare(before.ConnectionStrings, after.ConnectionStrings, c => c.Name, "Connection string", (a, b) => Details(
            (a.Value != b.Value, "value"), (a.Type != b.Type, "type"), (a.SlotSetting != b.SlotSetting, "slot setting"),
            (a.Name != b.Name, "name casing")), changes);
        return changes;
    }

    static string Details(params (bool Changed, string Label)[] parts) =>
        string.Join(", ", parts.Where(p => p.Changed).Select(p => p.Label));

    static void Compare<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> name, string section,
        Func<T, T, string> detail, List<ConfigChange> changes)
    {
        var old = before.GroupBy(name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var current = after.GroupBy(name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, item) in current)
        {
            if (!old.TryGetValue(key, out var previous)) changes.Add(new(ChangeKind.Added, section, name(item), ""));
            else if (detail(previous, item) is { Length: > 0 } d) changes.Add(new(ChangeKind.Changed, section, name(item), d));
        }
        foreach (var (key, item) in old)
            if (!current.ContainsKey(key)) changes.Add(new(ChangeKind.Removed, section, name(item), ""));
        changes.Sort((a, b) => (a.Section, a.Kind, a.Name).CompareTo((b.Section, b.Kind, b.Name)));
    }
}
