using System.Globalization;
using System.Text.Json;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

/// <summary>Reads health, facts, plan, slots, functions and metrics of an App Service / Function App through ARM, and restarts/stops/starts it.</summary>
public sealed class AppOverviewService(HttpClient http)
{
    const string ApiVersion = "2023-12-01";
    const string HealthApiVersion = "2022-10-01";
    const string MetricsApiVersion = "2023-10-01";

    public const string Hint = "Overview needs Reader on the app; restart/stop/start need Website Contributor " +
        "(Microsoft.Web/sites/restart/action, start/action, stop/action). Metrics need Monitoring Reader or Reader.";

    readonly AzureRest _rest = new(http);

    /// <summary>Metric name, display name and aggregation to show for web apps / function apps.</summary>
    public static readonly (string Name, string Display, string Aggregation)[] WebMetrics =
    [
        ("Requests", "Requests", "Total"), ("Http5xx", "Server errors (5xx)", "Total"), ("Http4xx", "Client errors (4xx)", "Total"),
        ("HttpResponseTime", "Response time", "Average"), ("CpuTime", "CPU time", "Total"),
        ("MemoryWorkingSet", "Memory working set", "Average"), ("HealthCheckStatus", "Health check status", "Average"),
    ];

    public static readonly (string Name, string Display, string Aggregation)[] FunctionMetrics =
    [
        ("FunctionExecutionCount", "Function executions", "Total"), ("Http5xx", "Server errors (5xx)", "Total"),
        ("Requests", "Requests", "Total"), ("MemoryWorkingSet", "Memory working set", "Average"),
    ];

    static readonly (string Name, string Display, string Aggregation)[] FallbackMetrics =
        [WebMetrics[0], WebMetrics[1]];

    /// <summary>Loads every section independently, in parallel. A failing section carries its error and never fails the others.</summary>
    public async Task<AppOverview> LoadAsync(WebAppRef app, CancellationToken ct, DateTimeOffset? now = null)
    {
        var site = GetSiteAsync(app, ct);
        var config = GetConfigAsync(app, ct);
        var health = GetHealthAsync(app, ct);
        var instances = GetInstancesAsync(app, ct);
        var slots = GetSlotsAsync(app, ct);
        var functions = app.IsFunction ? GetFunctionsAsync(app, ct) : Task.FromResult<Section<List<FunctionInfo>>>(null!);
        var metrics = GetMetricsAsync(app, ct, now);
        var siteSection = await site;
        var plan = await GetPlanAsync(app, siteSection.Value?.ServerFarmId ?? "", ct);
        return new AppOverview(siteSection, await config, plan, await health, await instances, await slots, await functions, await metrics);
    }

    public Task<Section<SiteInfo>> GetSiteAsync(WebAppRef app, CancellationToken ct) =>
        SectionAsync(async () => ParseSite(await GetAsync(app.Id, $"api-version={ApiVersion}", app, ct)), ct);

    public Task<Section<SiteConfigInfo>> GetConfigAsync(WebAppRef app, CancellationToken ct, IReadOnlyDictionary<string, string>? appSettings = null) =>
        SectionAsync(async () => ParseConfig(await GetAsync(app.Id + "/config/web", $"api-version={ApiVersion}", app, ct), appSettings), ct);

    public async Task<Section<PlanInfo>> GetPlanAsync(WebAppRef app, string serverFarmId, CancellationToken ct)
    {
        if (!serverFarmId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return new Section<PlanInfo>(null, serverFarmId.Length == 0 ? "App service plan is not known." : null);
        return await SectionAsync(async () => ParsePlan(await GetAsync(serverFarmId, $"api-version={ApiVersion}", app, ct)), ct);
    }

    public Task<Section<HealthInfo>> GetHealthAsync(WebAppRef app, CancellationToken ct) =>
        SectionAsync(async () => ParseHealth(await GetAsync(app.Id + "/providers/Microsoft.ResourceHealth/availabilityStatuses/current",
            $"api-version={HealthApiVersion}", app, ct)), ct);

    public Task<Section<List<InstanceInfo>>> GetInstancesAsync(WebAppRef app, CancellationToken ct) =>
        SectionAsync(async () => (await ListAsync(app, "/instances", ct)).Select(ParseInstance).ToList(), ct);

    public Task<Section<List<SlotInfo>>> GetSlotsAsync(WebAppRef app, CancellationToken ct) =>
        SectionAsync(async () => (await ListAsync(app, "/slots", ct)).Select(ParseSlot).ToList(), ct);

    public Task<Section<List<FunctionInfo>>> GetFunctionsAsync(WebAppRef app, CancellationToken ct) =>
        SectionAsync(async () => (await ListAsync(app, "/functions", ct)).Select(ParseFunction).ToList(), ct);

    Task<List<JsonElement>> ListAsync(WebAppRef app, string suffix, CancellationToken ct) =>
        _rest.ListAsync($"https://{AzureRest.ArmHost}{app.Id}{suffix}?api-version={ApiVersion}", app.Credential, Hint, ct);

    /// <summary>Metrics for the 24 hours before <paramref name="now"/> at one-hour granularity.</summary>
    public async Task<Section<List<MetricSeries>>> GetMetricsAsync(WebAppRef app, CancellationToken ct, DateTimeOffset? now = null)
    {
        var end = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var wanted = app.IsFunction ? FunctionMetrics : WebMetrics;
        return await SectionAsync(async () =>
        {
            try
            {
                return await QueryMetricsAsync(app, wanted, end, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest && IsUnknownMetric(ex.Message))
            {
                return await QueryMetricsAsync(app, FallbackMetrics, end, ct);
            }
        }, ct);
    }

    static bool IsUnknownMetric(string message) =>
        message.Contains("Failed to find metric", StringComparison.OrdinalIgnoreCase)
        || message.Contains("invalid metric", StringComparison.OrdinalIgnoreCase);

    async Task<List<MetricSeries>> QueryMetricsAsync(WebAppRef app, (string Name, string Display, string Aggregation)[] metrics,
        DateTimeOffset end, CancellationToken ct)
    {
        var start = end.AddHours(-24);
        const string format = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        var timespan = start.ToString(format, CultureInfo.InvariantCulture) + "/" + end.ToString(format, CultureInfo.InvariantCulture);
        var query = $"api-version={MetricsApiVersion}&timespan={Uri.EscapeDataString(timespan)}&interval=PT1H"
            + $"&metricnames={Uri.EscapeDataString(string.Join(",", metrics.Select(m => m.Name)))}&aggregation=Total,Average";
        using var document = await _rest.ArmQueryAsync(HttpMethod.Get, app.Id + "/providers/Microsoft.Insights/metrics", query,
            app.Credential, Hint, ct) ?? throw new InvalidOperationException("Azure returned no metrics.");
        return ParseMetrics(document.RootElement, metrics);
    }

    public Task RestartAsync(WebAppRef app, CancellationToken ct) =>
        ActionAsync(app, "/restart", $"api-version={ApiVersion}&softRestart=false&synchronous=false", ct);

    public Task StopAsync(WebAppRef app, CancellationToken ct) => ActionAsync(app, "/stop", $"api-version={ApiVersion}", ct);

    public Task StartAsync(WebAppRef app, CancellationToken ct) => ActionAsync(app, "/start", $"api-version={ApiVersion}", ct);

    async Task ActionAsync(WebAppRef app, string suffix, string query, CancellationToken ct)
    {
        try
        {
            using var _ = await _rest.ArmQueryAsync(HttpMethod.Post, app.Id + suffix, query, app.Credential, Hint, ct);
        }
        catch (JsonException)
        {
            // ARM accepted the request; the body is not needed.
        }
    }

    async Task<JsonElement> GetAsync(string path, string query, WebAppRef app, CancellationToken ct)
    {
        using var document = await _rest.ArmQueryAsync(HttpMethod.Get, path, query, app.Credential, Hint, ct)
            ?? throw new InvalidOperationException("Azure returned an empty response.");
        return document.RootElement.Clone();
    }

    static async Task<Section<T>> SectionAsync<T>(Func<Task<T>> load, CancellationToken ct)
    {
        try
        {
            return new Section<T>(await load());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new Section<T>(default, "The request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return new Section<T>(default, ex.Message);
        }
    }

    // ---- Pure, lenient parsing ----

    static JsonElement Props(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : element;

    static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;

    static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static DateTimeOffset? Time(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Json.Text(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    static List<string> Split(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static string LastSegment(string text) => text[(text.LastIndexOf('/') + 1)..];

    public static SiteInfo ParseSite(JsonElement root)
    {
        var p = Props(root);
        var hostNames = Child(p, "hostNames") is { ValueKind: JsonValueKind.Array } hosts
            ? hosts.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!).ToList() : [];
        return new SiteInfo(Json.Text(p, "state"), Json.Text(p, "usageState"), Json.Text(p, "availabilityState"), Json.Text(p, "defaultHostName"),
            hostNames, Bool(p, "httpsOnly"), Bool(p, "clientAffinityEnabled"), Json.Text(root, "location"), Json.Text(root, "kind"),
            Json.Text(p, "serverFarmId"), Time(p, "lastModifiedTimeUtc"), Split(Json.Text(p, "outboundIpAddresses")));
    }

    public static SiteConfigInfo ParseConfig(JsonElement root, IReadOnlyDictionary<string, string>? appSettings = null)
    {
        var p = Props(root);
        var linux = Json.Text(p, "linuxFxVersion");
        var net = Json.Text(p, "netFrameworkVersion");
        string runtime;
        if (linux.Length > 0) runtime = FriendlyRuntime(linux);
        else
        {
            runtime = net;
            foreach (var (property, label) in new[] { ("javaVersion", "Java"), ("nodeVersion", "Node"), ("pythonVersion", "Python"), ("phpVersion", "PHP") })
                if (Json.Text(p, property) is { Length: > 0 } v && v != "off") { runtime = $"{label} {v}"; break; }
            if (appSettings != null && appSettings.TryGetValue("FUNCTIONS_WORKER_RUNTIME", out var worker) && worker.Length > 0)
                runtime = runtime.Length > 0 ? $"{worker} ({runtime})" : worker;
        }
        return new SiteConfigInfo(linux, net, runtime, Bool(p, "alwaysOn"), Json.Text(p, "ftpsState"), Json.Text(p, "minTlsVersion"),
            Bool(p, "http20Enabled"), Json.Text(p, "healthCheckPath"), Int(p, "numberOfWorkers"), Bool(p, "use32BitWorkerProcess"));
    }

    /// <summary>Turns a Linux runtime stack such as "DOTNET-ISOLATED|8.0" or "NODE|20-lts" into ".NET 8.0 (isolated)" or "Node 20 LTS".</summary>
    public static string FriendlyRuntime(string linuxFxVersion)
    {
        var parts = linuxFxVersion.Split('|', 2);
        if (parts.Length < 2) return linuxFxVersion;
        var version = parts[1].Replace("-lts", " LTS", StringComparison.OrdinalIgnoreCase);
        return parts[0].ToUpperInvariant() switch
        {
            "DOTNETCORE" or "DOTNET" => $".NET {version}",
            "DOTNET-ISOLATED" => $".NET {version} (isolated)",
            "NODE" => $"Node {version}",
            "PYTHON" => $"Python {version}",
            "JAVA" => $"Java {version}",
            "PHP" => $"PHP {version}",
            "POWERSHELL" => $"PowerShell {version}",
            "DOCKER" or "COMPOSE" => $"Container ({version})",
            _ => linuxFxVersion,
        };
    }

    public static PlanInfo ParsePlan(JsonElement root)
    {
        var p = Props(root);
        var sku = Child(root, "sku");
        return new PlanInfo(Json.Text(root, "name"), Json.Text(sku, "name"), Json.Text(sku, "tier"), Int(sku, "capacity"),
            Json.Text(root, "kind"), Int(p, "numberOfSites"), Int(p, "maximumElasticWorkerCount"));
    }

    public static HealthInfo ParseHealth(JsonElement root)
    {
        var p = Props(root);
        var state = Json.Text(p, "availabilityState");
        return new HealthInfo(state.Length == 0 ? "Unknown" : state, Json.Text(p, "summary"), Json.Text(p, "reasonType"),
            Json.Text(p, "occuredTime"), Json.Text(p, "detailedStatus"));
    }

    public static InstanceInfo ParseInstance(JsonElement item)
    {
        var p = Props(item);
        var name = Json.Text(item, "name");
        return new InstanceInfo(LastSegment(name), Json.Text(p, "state"));
    }

    public static SlotInfo ParseSlot(JsonElement item)
    {
        var p = Props(item);
        return new SlotInfo(LastSegment(Json.Text(item, "name")), Json.Text(p, "state"), Json.Text(p, "defaultHostName"));
    }

    public static FunctionInfo ParseFunction(JsonElement item)
    {
        var p = Props(item);
        var trigger = "";
        if (Child(Child(p, "config"), "bindings") is { ValueKind: JsonValueKind.Array } bindings)
            trigger = bindings.EnumerateArray().Select(b => Json.Text(b, "type"))
                .FirstOrDefault(t => t.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase)) ?? "";
        return new FunctionInfo(LastSegment(Json.Text(item, "name")), Bool(p, "isDisabled") ?? false, trigger, Json.Text(p, "invoke_url_template"));
    }

    public static List<MetricSeries> ParseMetrics(JsonElement root, IEnumerable<(string Name, string Display, string Aggregation)>? known = null)
    {
        var table = (known ?? WebMetrics.Concat(FunctionMetrics)).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<MetricSeries>();
        if (Child(root, "value") is not { ValueKind: JsonValueKind.Array } values) return result;
        foreach (var metric in values.EnumerateArray())
        {
            var name = Json.Text(Child(metric, "name"), "value");
            if (name.Length == 0) continue;
            var data = Child(metric, "timeseries") is { ValueKind: JsonValueKind.Array } series && series.GetArrayLength() > 0
                && Child(series[0], "data") is { ValueKind: JsonValueKind.Array } d ? d.EnumerateArray().ToList() : [];
            table.TryGetValue(name, out var def);
            var aggregation = def.Aggregation ?? (data.Any(p => Number(p, "total") != null) ? "Total" : "Average");
            var key = aggregation == "Total" ? "total" : "average";
            var points = new List<(DateTimeOffset, double?)>();
            foreach (var point in data)
                if (Time(point, "timeStamp") is { } time) points.Add((time, Number(point, key)));
            var present = points.Where(p => p.Item2 != null).Select(p => p.Item2!.Value).ToList();
            double? summary = present.Count == 0 ? null : aggregation == "Total" ? present.Sum() : present.Average();
            var display = def.Display ?? (Json.Text(Child(metric, "name"), "localizedValue") is { Length: > 0 } l ? l : name);
            result.Add(new MetricSeries(name, display, Json.Text(metric, "unit"), aggregation, points, summary));
        }
        return result;
    }
}
