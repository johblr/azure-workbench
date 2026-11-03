using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureWorkbench.Services;

/// <summary>A pinned explorer item. Ids are ARM resource ids; no secrets are stored.</summary>
public sealed record Pin(string Kind, string Id, string Name, string? TenantId, string? Detail = null)
{
    // Kind: "queue", "topic", "subscription" (Id = namespace id, Name = entity, Detail = subscription), or "logs"/"config"/"deployments" (Id = site id).
    [JsonIgnore] public string Key => $"{Kind}|{Id}|{Name}|{Detail}".ToLowerInvariant();
}

public sealed class WorkbenchSettings
{
    public AuthMode? LastAuthMode { get; set; }
    public List<Pin> Pins { get; set; } = [];
    /// <summary>Opt-in: when no OS keyring exists, keep browser tokens in an unencrypted file in the user profile.</summary>
    public bool AllowUnencryptedTokenCache { get; set; }
}

/// <summary>
/// Per-user settings in the platform's app-data folder: %APPDATA%\AzureWorkbench on Windows,
/// ~/.config/AzureWorkbench on Linux and macOS (override with AZUREWORKBENCH_HOME).
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; }
    string FilePath => Path.Combine(Directory, "settings.json");

    public SettingsStore(string? directory = null)
    {
        var custom = Environment.GetEnvironmentVariable("AZUREWORKBENCH_HOME");
        Directory = directory ?? (!string.IsNullOrEmpty(custom) ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AzureWorkbench"));
    }

    public WorkbenchSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<WorkbenchSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(); // a corrupt or unreadable file must never stop the app starting
        }
    }

    public void Save(WorkbenchSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}
