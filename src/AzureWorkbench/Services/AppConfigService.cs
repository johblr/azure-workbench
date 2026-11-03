using System.Text.Json;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

public sealed class ConfigConflictException(string message) : InvalidOperationException(message);

/// <summary>Reads and writes App Service / Function App application settings and connection strings through ARM.</summary>
public sealed class AppConfigService(HttpClient http)
{
    const string ApiVersion = "2023-12-01";
    const string Hint = "Reading or changing configuration needs Website Contributor (or Microsoft.Web/sites/config/list/action and write) on the app.";
    readonly AzureRest _rest = new(http);

    public async Task<AppConfiguration> GetAsync(WebAppRef app, CancellationToken ct)
    {
        var settingsTask = PropertiesAsync(HttpMethod.Post, app, "config/appsettings/list", ct);
        var connectionTask = PropertiesAsync(HttpMethod.Post, app, "config/connectionstrings/list", ct);
        var slotTask = PropertiesAsync(HttpMethod.Get, app, "config/slotConfigNames", ct);
        await Task.WhenAll(settingsTask, connectionTask, slotTask);
        var slot = await slotTask;
        var stickySettings = Names(slot, "appSettingNames");
        var stickyConnections = Names(slot, "connectionStringNames");
        var settings = (await settingsTask).EnumerateObject()
            .Select(p => new AppSetting(p.Name, ValueText(p.Value), stickySettings.Contains(p.Name)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var connections = (await connectionTask).EnumerateObject()
            .Select(p => new ConnectionStringSetting(p.Name, ValueText(p.Value.TryGetProperty("value", out var v) ? v : default),
                ConnectionType(p.Value), stickyConnections.Contains(p.Name)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new AppConfiguration(settings, connections);
    }

    /// <summary>
    /// Saves <paramref name="updated"/> if Azure still matches <paramref name="loaded"/>; otherwise throws <see cref="ConfigConflictException"/>.
    /// Only sections that changed are written. Writing settings or connection strings restarts the app.
    /// </summary>
    public async Task<AppConfiguration> SaveAsync(WebAppRef app, AppConfiguration loaded, AppConfiguration updated, CancellationToken ct)
    {
        var (errors, _) = updated.Validate(app.IsLinux);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var current = await GetAsync(app, ct);
        if (!current.SameAs(loaded))
            throw new ConfigConflictException("Configuration changed in Azure since it was loaded. Reload, then reapply your edits.");

        var changes = Diff.Between(loaded, updated);
        bool Changed(string section, Func<ConfigChange, bool>? filter = null) =>
            changes.Any(c => c.Section == section && (filter?.Invoke(c) ?? true));
        static bool ValueOrMembership(ConfigChange c) => c.Kind != ChangeKind.Changed || c.Detail != "slot setting";

        if (Changed("Setting", ValueOrMembership))
            await PropertiesAsync(HttpMethod.Put, app, "config/appsettings", ct,
                updated.Settings.ToDictionary(s => s.Name, s => s.Value));
        if (Changed("Connection string", ValueOrMembership))
            await PropertiesAsync(HttpMethod.Put, app, "config/connectionstrings", ct,
                updated.ConnectionStrings.ToDictionary(c => c.Name, c => new { value = c.Value, type = c.Type }));

        var settingNames = updated.Settings.Where(s => s.SlotSetting).Select(s => s.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var connectionNames = updated.ConnectionStrings.Where(c => c.SlotSetting).Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var slot = await PropertiesAsync(HttpMethod.Get, app, "config/slotConfigNames", ct);
        var existingSettings = Names(slot, "appSettingNames");
        var existingConnections = Names(slot, "connectionStringNames");
        // Keep names Azure tracks that are not in this app's lists (e.g. sticky names reserved for other slots).
        bool Unlisted(string n, IEnumerable<string> names) => !names.Contains(n, StringComparer.OrdinalIgnoreCase);
        var keepSettings = existingSettings.Where(n => Unlisted(n, updated.Settings.Select(s => s.Name)) && Unlisted(n, loaded.Settings.Select(s => s.Name)));
        var keepConnections = existingConnections.Where(n => Unlisted(n, updated.ConnectionStrings.Select(c => c.Name)) && Unlisted(n, loaded.ConnectionStrings.Select(c => c.Name)));
        var newSettings = settingNames.Concat(keepSettings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newConnections = connectionNames.Concat(keepConnections).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!newSettings.SetEquals(existingSettings) || !newConnections.SetEquals(existingConnections))
        {
            var body = new Dictionary<string, object?>();
            foreach (var property in slot.EnumerateObject()) body[property.Name] = property.Value; // preserve azureStorageConfigNames etc.
            body["appSettingNames"] = newSettings.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            body["connectionStringNames"] = newConnections.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            await PropertiesAsync(HttpMethod.Put, app, "config/slotConfigNames", ct, body);
        }
        return await GetAsync(app, ct);
    }

    async Task<JsonElement> PropertiesAsync(HttpMethod method, WebAppRef app, string path, CancellationToken ct, object? properties = null)
    {
        using var document = await _rest.ArmAsync(method, $"{app.Id}/{path}", ApiVersion, app.Credential,
            properties == null ? null : new { properties }, Hint, ct);
        return document.RootElement.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object
            ? p.Clone()
            : JsonDocument.Parse("{}").RootElement.Clone();
    }

    static HashSet<string> Names(JsonElement properties, string key) =>
        properties.TryGetProperty(key, out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    // ARM returns the type as an enum name; older API versions return its integer value.
    static string ConnectionType(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var type)) return "Custom";
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var index))
            return index switch
            {
                0 => "MySql", 1 => "SQLServer", 2 => "SQLAzure", 3 => "Custom", 4 => "NotificationHub", 5 => "ServiceBus",
                6 => "EventHub", 7 => "ApiHub", 8 => "DocDb", 9 => "RedisCache", 10 => "PostgreSQL", _ => "Custom",
            };
        return type.ValueKind == JsonValueKind.String ? AppConfiguration.CanonicalType(type.GetString()!) : "Custom";
    }
}
