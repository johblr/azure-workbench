using System.Text.Json;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

/// <summary>Turns stored pins back into live resource references after sign-in.</summary>
public sealed class PinResolver(CredentialProvider credentials, HttpClient http)
{
    readonly AzureRest _rest = new(http);

    public static Pin For(QueueRef entity, string? tenantId) => new(
        entity.KindLabel, entity.Namespace.Resource.Id.ToString(), entity.Name, tenantId, entity.Subscription);

    public static Pin For(AppPage page, string? tenantId) => new(page.Kind switch
    {
        AppPageKind.Configuration => "config",
        AppPageKind.Deployments => "deployments",
        _ => "logs",
    }, page.App.Id, page.App.Name, tenantId);

    public static string Describe(Pin pin) => pin.Kind switch
    {
        "subscription" => $"{pin.Name}/{pin.Detail}",
        "logs" => $"{pin.Name} · logs",
        "config" => $"{pin.Name} · configuration",
        "deployments" => $"{pin.Name} · deployments",
        _ => pin.Name,
    };

    /// <summary>Returns a <see cref="QueueRef"/> or an <see cref="AppPage"/>.</summary>
    public async Task<object> ResolveAsync(Pin pin, CancellationToken ct)
    {
        var credential = credentials.For(pin.TenantId);
        switch (pin.Kind)
        {
            case "queue" or "topic" or "subscription":
            {
                var id = new ResourceIdentifier(pin.Id);
                var ns = (await new ArmClient(credential).GetServiceBusNamespaceResource(id).GetAsync(ct)).Value;
                var nsRef = new NamespaceRef(credential, ns);
                return new QueueRef(nsRef, pin.Name, pin.Kind == "subscription" ? pin.Detail : null, pin.Kind == "topic");
            }
            case "logs" or "config" or "deployments":
            {
                using var site = await _rest.ArmAsync(HttpMethod.Get, pin.Id, "2023-12-01", credential, null,
                    "The pinned app may have been deleted or you may no longer have access.", ct);
                var root = site.RootElement;
                var app = new WebAppRef(Json.Required(root, "id"), Json.Required(root, "name"), Json.Text(root, "kind"), credential, InsightsLink(root));
                var kind = pin.Kind switch { "config" => AppPageKind.Configuration, "deployments" => AppPageKind.Deployments, _ => AppPageKind.Logs };
                return new AppPage(app, kind);
            }
            default:
                throw new InvalidOperationException($"Unknown pin type '{pin.Kind}'.");
        }
    }

    static string? InsightsLink(JsonElement resource)
    {
        if (resource.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.EndsWith("/app-insights-resource-id", StringComparison.OrdinalIgnoreCase))
                    return tag.Value.GetString();
        return null;
    }
}
