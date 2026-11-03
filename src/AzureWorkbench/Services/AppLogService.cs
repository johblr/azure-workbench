using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

public sealed class AppLogService(HttpClient http)
{
    const string ArmScope = AzureRest.ArmScope;
    const string InsightsScope = "https://api.applicationinsights.io/.default";
    const string ArmHost = AzureRest.ArmHost;
    const string Hint = "Check monitoring permissions, SCM access restrictions and whether this hosting plan supports streaming.";
    readonly AzureRest _rest = new(http);

    public async Task<List<WebAppRef>> GetAppsAsync(string subscriptionId, TokenCredential credential, CancellationToken ct)
    {
        var resources = await ListAsync(
            $"https://{ArmHost}/subscriptions/{subscriptionId}/providers/Microsoft.Web/sites?api-version=2023-12-01",
            credential, ct);
        return resources.Where(r => !Text(r, "kind").Contains("workflowapp", StringComparison.OrdinalIgnoreCase))
            .Select(r => new WebAppRef(Required(r, "id"), Required(r, "name"), Text(r, "kind"), credential, InsightsLink(r)))
            .OrderBy(a => a.Name).ToList();
    }

    public async Task<List<InsightsRef>> GetInsightsAsync(WebAppRef app, CancellationToken ct)
    {
        var resources = await ListAsync(
            $"https://{ArmHost}/subscriptions/{app.SubscriptionId}/providers/Microsoft.Insights/components?api-version=2020-02-02",
            app.Credential, ct);
        if (app.InsightsResourceId is { } linked
            && !resources.Any(r => string.Equals(Text(r, "id"), linked, StringComparison.OrdinalIgnoreCase)))
        {
            if (!linked.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || !linked.Contains("/providers/microsoft.insights/components/", StringComparison.OrdinalIgnoreCase)
                || linked.Contains('?') || linked.Contains('#'))
                throw new InvalidOperationException("The app has an invalid Application Insights resource link.");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{ArmHost}{linked}?api-version=2020-02-02");
            using var response = await SendAsync(request, app.Credential, ArmScope, ct);
            using var document = await ReadJsonAsync(response, ct);
            resources.Add(document.RootElement.Clone());
        }
        return resources.Select(r => new InsightsRef(Required(r, "id"), Required(r, "name"), Required(r.GetProperty("properties"), "AppId")))
            .OrderBy(a => a.Name).ToList();
    }

    public async Task<List<LogEntry>> QueryAsync(WebAppRef app, InsightsRef insights, int minutes, int severity,
        string search, string role, CancellationToken ct, TelemetryTypes types = TelemetryTypes.Default, string operationId = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.applicationinsights.io/v1/apps/{Uri.EscapeDataString(insights.AppId)}/query");
        request.Content = JsonContent.Create(new { query = BuildQuery(minutes, severity, search, role, types, operationId) });
        using var response = await SendAsync(request, app.Credential, InsightsScope, ct);
        using var document = await ReadJsonAsync(response, ct);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"Application Insights returned an incomplete query: {error}");
        var table = root.GetProperty("tables").EnumerateArray().FirstOrDefault(t => Text(t, "name") == "PrimaryResult");
        if (table.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Application Insights did not return a result table.");
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => Required(c, "name")).ToList();
        int Column(string name) => columns.IndexOf(name) is var i && i >= 0 ? i
            : throw new InvalidOperationException($"Missing log column '{name}'.");
        var timestamp = Column("timestamp");
        var level = Column("severityLevel");
        var roleColumn = Column("cloud_RoleName");
        var message = Column("message");
        // Older/partial results may lack these; they are optional.
        var operation = columns.IndexOf("operation_Id");
        var itemType = columns.IndexOf("itemType");
        return table.GetProperty("rows").EnumerateArray()
            .Select(row => new LogEntry(row[timestamp].ToString(), row[level].ToString(),
                row[roleColumn].ToString(), row[message].ToString(),
                operation >= 0 ? row[operation].ToString() : "", itemType >= 0 ? row[itemType].ToString() : "")).ToList();
    }

    /// <summary>
    /// Builds the recent-logs query. With <paramref name="operationId"/> the same time window (minutes) still applies,
    /// so use up to 1440 minutes to find an operation anywhere in the last day. All user input is escaped as KQL literals.
    /// </summary>
    public static string BuildQuery(int minutes, int severity, string search, string role,
        TelemetryTypes types = TelemetryTypes.Default, string? operationId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 1440);
        ArgumentOutOfRangeException.ThrowIfLessThan(severity, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(severity, 4);
        if ((types & TelemetryTypes.All) == 0) throw new ArgumentException("Select at least one telemetry type.", nameof(types));
        const string common = "timestamp, severityLevel, cloud_RoleName, message, operation_Id, itemType";
        var tables = new List<string>();
        if (types.HasFlag(TelemetryTypes.Traces))
            tables.Add($"(traces | project timestamp, severityLevel = toint(severityLevel), cloud_RoleName, message, operation_Id, itemType)");
        if (types.HasFlag(TelemetryTypes.Exceptions))
            tables.Add($"(exceptions | project timestamp, severityLevel = toint(severityLevel), cloud_RoleName, message=outerMessage, operation_Id, itemType)");
        if (types.HasFlag(TelemetryTypes.Requests))
            tables.Add("(requests | project timestamp, severityLevel = iff(tobool(success), 1, 3), cloud_RoleName, "
                + "message=strcat(name, \" \", resultCode, \" \", tostring(toint(duration)), \"ms\"), operation_Id, itemType)");
        if (types.HasFlag(TelemetryTypes.Dependencies))
            tables.Add("(dependencies | project timestamp, severityLevel = iff(tobool(success), 1, 3), cloud_RoleName, "
                + "message=strcat(type, \" \", target, \" \", name, \" \", resultCode, \" \", tostring(toint(duration)), \"ms\"), operation_Id, itemType)");
        return $"""
            union isfuzzy=true
                {string.Join(",\n    ", tables)}
            | where timestamp > ago({minutes}m)
            | where coalesce(severityLevel, 1) >= {severity}
            | where message contains {Literal(search)}
            { (string.IsNullOrWhiteSpace(role) ? "" : $"| where cloud_RoleName =~ {Literal(role.Trim())}") }
            { (string.IsNullOrWhiteSpace(operationId) ? "" : $"| where operation_Id == {Literal(operationId.Trim())}") }
            | order by timestamp desc
            | take 500
            | project {common}
            """;
    }

    public async IAsyncEnumerable<string> TailAsync(WebAppRef app, [EnumeratorCancellation] CancellationToken ct)
    {
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://{ArmHost}{app.Id}?api-version=2023-12-01");
        using var metadataResponse = await SendAsync(metadataRequest, app.Credential, ArmScope, ct);
        using var metadata = await ReadJsonAsync(metadataResponse, ct);
        var properties = metadata.RootElement.GetProperty("properties");
        var hosts = properties.TryGetProperty("hostNameSslStates", out var states)
            ? states.EnumerateArray().Where(h => Text(h, "hostType") == "Repository").Select(h => Text(h, "name"))
            : Enumerable.Empty<string>();
        var host = hosts.FirstOrDefault();
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException("No SCM/Kudu endpoint is available for this app. This hosting plan may not support live log streaming.");
        // Never send an ARM bearer token to a custom or untrusted hostname.
        if (Uri.CheckHostName(host) != UriHostNameType.Dns
            || !host.Contains(".scm.", StringComparison.OrdinalIgnoreCase)
            || !host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported SCM host '{host}'. Only public Azure SCM endpoints are supported.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/logstream");
        using var response = await SendAsync(request, app.Credential, ArmScope, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
            yield return new string(buffer, 0, read);
    }

    Task<List<JsonElement>> ListAsync(string url, TokenCredential credential, CancellationToken ct) =>
        _rest.ListAsync(url, credential, Hint, ct);

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TokenCredential credential, string scope, CancellationToken ct) =>
        _rest.SendAsync(request, credential, scope, Hint, ct);

    static Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        AzureRest.ReadJsonAsync(response, ct);

    static string? InsightsLink(JsonElement resource)
    {
        if (resource.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.EndsWith("/app-insights-resource-id", StringComparison.OrdinalIgnoreCase))
                    return tag.Value.GetString();
        return null;
    }

    static string Text(JsonElement element, string property) => Json.Text(element, property);

    static string Required(JsonElement element, string property) => Json.Required(element, property);

    static string Literal(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "'";
}
