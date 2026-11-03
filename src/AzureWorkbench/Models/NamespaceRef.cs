using Azure.Core;
using Azure.ResourceManager.ServiceBus;

namespace AzureWorkbench.Models;

public record NamespaceRef(TokenCredential Credential, ServiceBusNamespaceResource Resource)
{
    public string Name => Resource.Data.Name;
    public string Host => new Uri(Resource.Data.ServiceBusEndpoint).Host;
}
