using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace AzureWorkbench.Services;

/// <summary>Minimal authenticated REST client for ARM and Azure data-plane endpoints, shared by the app services.</summary>
public sealed class AzureRest(HttpClient http)
{
    public const string ArmScope = "https://management.azure.com/.default";
    public const string ArmHost = "management.azure.com";
    static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Sends an authenticated request. Failures throw <see cref="HttpRequestException"/> with <paramref name="hint"/> and any Azure error message.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TokenCredential credential, string scope,
        string hint, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(HeaderTimeout);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var detail = await ErrorDetailAsync(response, ct);
        response.Dispose();
        throw new HttpRequestException($"Request failed: {(int)status} {status}.{detail} {hint}".TrimEnd(), null, status);
    }

    /// <summary>Calls an ARM path (starting with '/') and returns the parsed JSON response.</summary>
    public async Task<JsonDocument> ArmAsync(HttpMethod method, string path, string apiVersion, TokenCredential credential,
        object? body, string hint, CancellationToken ct)
    {
        if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("ARM paths must be absolute resource paths without a query.", nameof(path));
        using var request = new HttpRequestMessage(method, $"https://{ArmHost}{path}?api-version={apiVersion}");
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, credential, ArmScope, hint, ct);
        return await ReadJsonAsync(response, ct);
    }

    /// <summary>Lists an ARM collection, following nextLink only to the ARM host.</summary>
    public async Task<List<JsonElement>> ListAsync(string url, TokenCredential credential, string hint, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var uri = new Uri(next);
            if (uri.Scheme != "https" || uri.Host != ArmHost)
                throw new InvalidOperationException("Azure returned an unexpected resource-list URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SendAsync(request, credential, ArmScope, hint, ct);
            using var document = await ReadJsonAsync(response, ct);
            result.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(r => r.Clone()));
            next = document.RootElement.TryGetProperty("nextLink", out var link) ? link.GetString() : null;
        }
        return result;
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeaderTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
    }

    static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (text.Length > 8192) return "";
            using var document = JsonDocument.Parse(text);
            var error = document.RootElement.TryGetProperty("error", out var e) ? e : document.RootElement;
            var message = Json.Text(error, "message");
            if (message.Length == 0) message = Json.Text(error, "Message");
            return message.Length == 0 ? "" : " " + message.Trim();
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or OperationCanceledException or IOException)
        {
            return "";
        }
    }
}

static class Json
{
    public static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    public static string Required(JsonElement element, string property) =>
        Text(element, property) is { Length: > 0 } value ? value
            : throw new InvalidOperationException($"Azure response is missing '{property}'.");
}
