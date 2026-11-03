namespace AzureWorkbench.Models;

public enum EntityKind { Queue, Topic, Subscription }

/// <summary>
/// A Service Bus messaging entity: a queue, a topic (send only) or a topic subscription (receive; sends go to its topic).
/// </summary>
public record QueueRef(NamespaceRef Namespace, string Name, string? Subscription = null, bool IsTopic = false)
{
    public EntityKind Kind => Subscription != null ? EntityKind.Subscription : IsTopic ? EntityKind.Topic : EntityKind.Queue;
    public bool CanReceive => Kind != EntityKind.Topic;
    /// <summary>The entity messages are sent to: the queue, or the topic for topics and subscriptions.</summary>
    public string SendTarget => Name;
    public string DisplayName => Subscription != null ? $"{Name}/{Subscription}" : Name;
    /// <summary>ARM-style path relative to the namespace, used for stable tab keys.</summary>
    public string ResourcePath => Kind switch
    {
        EntityKind.Queue => $"{Namespace.Resource.Id}/queues/{Name}",
        EntityKind.Topic => $"{Namespace.Resource.Id}/topics/{Name}",
        _ => $"{Namespace.Resource.Id}/topics/{Name}/subscriptions/{Subscription}",
    };
    public string KindLabel => Kind switch { EntityKind.Queue => "queue", EntityKind.Topic => "topic", _ => "subscription" };
}
