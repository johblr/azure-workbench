using System.Globalization;
using System.Text;
using System.Text.Json;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

/// <summary>Reads App Service / Function App deployment history and logs through ARM (and Kudu for nested log details).</summary>
public sealed class DeploymentService(HttpClient http)
{
    const string ApiVersion = "2023-12-01";
    public const string Hint = "Deployment history needs Reader access on the app; logs may also need Website Contributor. " +
        "Flex Consumption and some container deployments record little or no history here.";
    readonly AzureRest _rest = new(http);

    /// <summary>All recorded deployments, newest first.</summary>
    public async Task<List<Deployment>> ListAsync(WebAppRef app, CancellationToken ct)
    {
        var items = await _rest.ListAsync($"https://{AzureRest.ArmHost}{app.Id}/deployments?api-version={ApiVersion}", app.Credential, Hint, ct);
        return items.Select(Parse).OrderByDescending(d => d.Start ?? DateTimeOffset.MinValue).ToList();
    }

    public async Task<List<DeploymentLogEntry>> GetLogAsync(WebAppRef app, string deploymentId, CancellationToken ct)
    {
        var url = $"https://{AzureRest.ArmHost}{app.Id}/deployments/{Uri.EscapeDataString(deploymentId)}/log?api-version={ApiVersion}";
        var items = await _rest.ListAsync(url, app.Credential, Hint, ct);
        return items.Select(item =>
        {
            var p = Props(item);
            var id = LastSegment(Json.Text(p, "details_url"));
            return new DeploymentLogEntry(Time(p, "log_time"), Json.Text(p, "message"), id.Length > 0 ? id : null);
        }).ToList();
    }

    /// <summary>
    /// Fetches the nested log of a log entry from the app's Kudu (SCM) site, or null when that endpoint is not a public
    /// Azure SCM host. The ARM token is never sent anywhere else.
    /// </summary>
    public async Task<string?> GetLogDetailsAsync(WebAppRef app, string deploymentId, string detailsId, CancellationToken ct)
    {
        using var metadata = await _rest.ArmAsync(HttpMethod.Get, app.Id, ApiVersion, app.Credential, null, Hint, ct);
        var host = ScmHost(metadata.RootElement);
        return host == null ? null : await DetailsAsync(app, host, deploymentId, detailsId, ct);
    }

    /// <summary>
    /// Fetches several nested Kudu logs (e.g. build output) with one metadata lookup. Returns detailsId → text; empty when the
    /// app has no trusted public SCM endpoint. Individual failures are returned as an error line rather than failing the batch.
    /// </summary>
    public async Task<Dictionary<string, string>> GetLogDetailsAsync(WebAppRef app, string deploymentId, IEnumerable<string> detailsIds, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var metadata = await _rest.ArmAsync(HttpMethod.Get, app.Id, ApiVersion, app.Credential, null, Hint, ct);
        if (ScmHost(metadata.RootElement) is not { } host) return result;
        foreach (var id in detailsIds.Distinct())
        {
            try { result[id] = await DetailsAsync(app, host, deploymentId, id, ct); }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { result[id] = $"(details unavailable: {ex.Message})"; }
        }
        return result;
    }

    async Task<string> DetailsAsync(WebAppRef app, string host, string deploymentId, string detailsId, CancellationToken ct)
    {
        var url = $"https://{host}/api/deployments/{Uri.EscapeDataString(deploymentId)}/log/{Uri.EscapeDataString(detailsId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _rest.SendAsync(request, app.Credential, AzureRest.ArmScope, Hint, ct);
        using var document = await AzureRest.ReadJsonAsync(response, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return document.RootElement.GetRawText();
        var text = new StringBuilder();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var time = Time(item, "log_time");
            text.AppendLine($"{(time is { } t ? t.ToLocalTime().ToString("HH:mm:ss") + "  " : "")}{Json.Text(item, "message")}");
        }
        return text.ToString().TrimEnd();
    }

    static string? ScmHost(JsonElement site)
    {
        if (!site.TryGetProperty("properties", out var p) || !p.TryGetProperty("hostNameSslStates", out var states)
            || states.ValueKind != JsonValueKind.Array) return null;
        var host = states.EnumerateArray().Where(h => Json.Text(h, "hostType") == "Repository").Select(h => Json.Text(h, "name"))
            .FirstOrDefault(h => h.Length > 0);
        // Never send an ARM bearer token to a custom or untrusted hostname.
        if (host == null || Uri.CheckHostName(host) != UriHostNameType.Dns
            || !host.Contains(".scm.", StringComparison.OrdinalIgnoreCase)
            || !host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase)) return null;
        return host;
    }

    static Deployment Parse(JsonElement item)
    {
        var p = Props(item);
        var id = LastSegment(Json.Text(item, "name"));
        if (id.Length == 0) id = LastSegment(Json.Text(item, "id"));
        var status = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
            && s.TryGetInt32(out var n) ? n : -1;
        var active = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
        return new Deployment(id, status, Deployment.StatusName(status), Json.Text(p, "author"), Json.Text(p, "deployer"),
            Json.Text(p, "message").Trim(), Time(p, "start_time"), Time(p, "end_time"), active);
    }

    static JsonElement Props(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("properties", out var p) ? p : default;

    static DateTimeOffset? Time(JsonElement element, string property) =>
        DateTimeOffset.TryParse(Json.Text(element, property), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    static string LastSegment(string value)
    {
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : value;
        return Uri.UnescapeDataString(path.TrimEnd('/').Split('/').LastOrDefault() ?? "");
    }
}
