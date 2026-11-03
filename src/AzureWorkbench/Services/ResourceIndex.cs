using Azure.ResourceManager.Resources;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

public sealed record IndexedEntity(QueueRef Entity, string? TenantId, string Subscription);
public sealed record IndexedApp(WebAppRef App, string? TenantId, string Subscription);

/// <summary>
/// Background catalogue of every Service Bus entity and app the signed-in identity can list, so quick open,
/// the explorer search, replay targets and "Find in logs" are not limited to what the tree has expanded.
/// Read-only: it only lists resources. Call from the UI thread; results are appended as each subscription finishes.
/// </summary>
public sealed class ResourceIndex(ResourceExplorer explorer, AppLogService apps, CredentialProvider credentials)
{
    const int Parallelism = 4;
    readonly List<IndexedEntity> _entities = [];
    readonly List<IndexedApp> _apps = [];
    readonly HashSet<string> _indexedTenants = new(StringComparer.OrdinalIgnoreCase);
    int _running;

    public IReadOnlyList<IndexedEntity> Entities => _entities;
    public IReadOnlyList<IndexedApp> Apps => _apps;
    public bool IsIndexing => _running > 0;
    public int Failures { get; private set; }

    /// <summary>Raised after each subscription is indexed and when indexing finishes.</summary>
    public event Action? Changed;

    public void Clear()
    {
        _entities.Clear();
        _apps.Clear();
        _indexedTenants.Clear();
        Failures = 0;
        Changed?.Invoke();
    }

    /// <summary>Indexes a tenant once per session (forced re-index with <paramref name="refresh"/>).</summary>
    public async Task IndexTenantAsync(string? tenantId, CancellationToken ct, bool refresh = false)
    {
        var key = tenantId ?? "";
        if (!refresh && !_indexedTenants.Add(key)) return;
        if (refresh)
        {
            _indexedTenants.Add(key);
            _entities.RemoveAll(e => e.TenantId == tenantId);
            _apps.RemoveAll(a => a.TenantId == tenantId);
        }
        _running++;
        Changed?.Invoke();
        try
        {
            var subscriptions = await explorer.GetSubscriptionsAsync(tenantId);
            using var gate = new SemaphoreSlim(Parallelism);
            await Task.WhenAll(subscriptions.Select(async subscription =>
            {
                await gate.WaitAsync(ct);
                try { await IndexSubscriptionAsync(subscription, tenantId, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { Failures++; } // access gaps in one subscription must not stop the rest
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Failures++; _indexedTenants.Remove(key); }
        finally
        {
            _running--;
            Changed?.Invoke();
        }
    }

    async Task IndexSubscriptionAsync(SubscriptionResource subscription, string? tenantId, CancellationToken ct)
    {
        var name = subscription.Data.DisplayName;
        var appsTask = apps.GetAppsAsync(subscription.Id.SubscriptionId!, credentials.For(tenantId), ct);
        var entities = new List<IndexedEntity>();
        foreach (var ns in await explorer.GetNamespacesAsync(subscription, tenantId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var queues = ResourceExplorer.GetQueuesAsync(ns);
                var topics = ResourceExplorer.GetTopicsAsync(ns);
                await Task.WhenAll(queues, topics);
                entities.AddRange((await queues).Select(q => new IndexedEntity(new QueueRef(ns, q.Data.Name), tenantId, name)));
                foreach (var topic in await topics)
                {
                    var topicRef = new QueueRef(ns, topic.Data.Name, IsTopic: true);
                    entities.Add(new IndexedEntity(topicRef, tenantId, name));
                    entities.AddRange((await ResourceExplorer.GetSubscriptionsAsync(topic)).Select(s =>
                        new IndexedEntity(topicRef with { Subscription = s.Data.Name, IsTopic = false }, tenantId, name)));
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { Failures++; }
        }
        List<WebAppRef> found;
        try { found = await appsTask; }
        catch (Exception) when (!ct.IsCancellationRequested) { found = []; Failures++; }
        ct.ThrowIfCancellationRequested();
        _entities.AddRange(entities);
        _apps.AddRange(found.Select(a => new IndexedApp(a, tenantId, name)));
        Changed?.Invoke();
    }
}
