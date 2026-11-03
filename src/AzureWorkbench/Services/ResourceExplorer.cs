using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

public class ResourceExplorer(CredentialProvider credentials)
{
    public async Task<List<TenantResource>> GetTenantsAsync() =>
        (await new ArmClient(credentials.For(null)).GetTenants().GetAllAsync().ToListAsync())
            .OrderBy(t => t.Data.DisplayName).ToList();

    public async Task<List<SubscriptionResource>> GetSubscriptionsAsync(string? tenantId) =>
        (await new ArmClient(credentials.For(tenantId)).GetSubscriptions().GetAllAsync().ToListAsync())
            .Where(s => tenantId == null || s.Data.TenantId?.ToString() == tenantId)
            .OrderBy(s => s.Data.DisplayName).ToList();

    public async Task<List<NamespaceRef>> GetNamespacesAsync(SubscriptionResource subscription, string? tenantId) =>
        (await subscription.GetServiceBusNamespacesAsync().ToListAsync())
            .OrderBy(n => n.Data.Name)
            .Select(n => new NamespaceRef(credentials.For(tenantId), n)).ToList();

    public static async Task<List<ServiceBusTopicResource>> GetTopicsAsync(NamespaceRef ns) =>
        (await ns.Resource.GetServiceBusTopics().GetAllAsync().ToListAsync())
            .OrderBy(t => t.Data.Name).ToList();

    public static async Task<List<ServiceBusSubscriptionResource>> GetSubscriptionsAsync(ServiceBusTopicResource topic) =>
        (await topic.GetServiceBusSubscriptions().GetAllAsync().ToListAsync())
            .OrderBy(s => s.Data.Name).ToList();

    public static async Task<List<ServiceBusQueueResource>> GetQueuesAsync(NamespaceRef ns) =>
        (await ns.Resource.GetServiceBusQueues().GetAllAsync().ToListAsync())
            .OrderBy(q => q.Data.Name).ToList();
}
