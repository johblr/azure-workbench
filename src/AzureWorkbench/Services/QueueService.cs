using Azure.Messaging.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

/// <summary>Service Bus data-plane operations. Keeps one AMQP connection per namespace, since opening one costs about a second.</summary>
public sealed class QueueService : IAsyncDisposable
{
    // Cache lookup runs on the UI thread before awaiting; SDK clients support concurrent tab operations.
    readonly Dictionary<string, ServiceBusClient> _clients = new();
    readonly Func<QueueRef, Task<bool>> _duplicateDetection;

    /// <summary>Name of the application property that records a copy's original message ID.</summary>
    public const string OriginalMessageIdProperty = "OriginalMessageId";

    /// <param name="duplicateDetection">Override for <see cref="RequiresDuplicateDetectionAsync"/> (checks use it); defaults to reading the entity from ARM.</param>
    public QueueService(Func<QueueRef, Task<bool>>? duplicateDetection = null) =>
        _duplicateDetection = duplicateDetection ?? ReadDuplicateDetectionAsync;

    ServiceBusClient Client(QueueRef q)
    {
        if (!_clients.TryGetValue(q.Namespace.Host, out var client))
            _clients[q.Namespace.Host] = client = new ServiceBusClient(q.Namespace.Host, q.Namespace.Credential);
        return client;
    }

    /// <summary>Closes all connections; call when the credential changes.</summary>
    public async Task ResetAsync()
    {
        var clients = _clients.Values.ToList();
        _clients.Clear();
        foreach (var c in clients) await c.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(ResetAsync());

    static ServiceBusReceiver Receiver(ServiceBusClient client, QueueRef q, bool deadLetter,
        ServiceBusReceiveMode mode = ServiceBusReceiveMode.PeekLock)
    {
        if (!q.CanReceive) throw new InvalidOperationException("Topics hold no messages. Open one of its subscriptions to receive.");
        var options = new ServiceBusReceiverOptions { SubQueue = deadLetter ? SubQueue.DeadLetter : SubQueue.None, ReceiveMode = mode };
        return q.Subscription is { } subscription
            ? client.CreateReceiver(q.Name, subscription, options)
            : client.CreateReceiver(q.Name, options);
    }

    // Wait longer for the first batch (link warm-up); after that an empty second means the queue is drained.
    static TimeSpan FirstOrNext(int receivedSoFar) => TimeSpan.FromSeconds(receivedSoFar == 0 ? 3 : 1);

    /// <summary>
    /// Peeks messages without locking them. <paramref name="fromSequenceNumber"/> continues after a previous peek
    /// (pass the last sequence number + 1); null starts from the beginning.
    /// </summary>
    public async Task<List<ServiceBusReceivedMessage>> PeekAsync(QueueRef q, bool deadLetter, int count, long? fromSequenceNumber = null)
    {
        var client = Client(q);
        await using var receiver = Receiver(client, q, deadLetter, ServiceBusReceiveMode.PeekLock);
        var got = new List<ServiceBusReceivedMessage>();
        var from = fromSequenceNumber;
        while (got.Count < count)
        {
            var batch = from is { } seq
                ? await receiver.PeekMessagesAsync(count - got.Count, seq)
                : await receiver.PeekMessagesAsync(count - got.Count);
            if (batch.Count == 0) break;
            got.AddRange(batch);
            from = batch[^1].SequenceNumber + 1;
        }
        return got;
    }

    public async Task<List<ServiceBusReceivedMessage>> ReceiveAndDeleteAsync(QueueRef q, bool deadLetter, int count)
    {
        var client = Client(q);
        await using var receiver = Receiver(client, q, deadLetter, ServiceBusReceiveMode.ReceiveAndDelete);
        var got = new List<ServiceBusReceivedMessage>();
        while (got.Count < count)
        {
            var batch = await receiver.ReceiveMessagesAsync(count - got.Count, FirstOrNext(got.Count));
            if (batch.Count == 0) break;
            got.AddRange(batch);
        }
        return got;
    }

    /// <summary>
    /// Sends copies of the messages to <paramref name="target"/> (its send target; on its own namespace connection).
    /// Returns true when the copies were given new message IDs because the target uses duplicate detection.
    /// </summary>
    public async Task<bool> ResendAsync(QueueRef target, IEnumerable<ServiceBusReceivedMessage> messages)
    {
        var newIds = await RequiresDuplicateDetectionAsync(target);
        await SendManyAsync(target, messages.Select(m => Copy(m, newIds)));
        return newIds;
    }

    /// <summary>
    /// Whether the entity that receives sends for <paramref name="target"/> (the queue, or the topic for topics and subscriptions)
    /// discards messages whose ID it has already seen. Errs on the side of true when it can't be read, because a copy that
    /// keeps its ID can then be silently dropped.
    /// </summary>
    public Task<bool> RequiresDuplicateDetectionAsync(QueueRef target) => _duplicateDetection(target);

    static async Task<bool> ReadDuplicateDetectionAsync(QueueRef target)
    {
        try
        {
            return target.Kind == EntityKind.Queue
                ? (await target.Namespace.Resource.GetServiceBusQueueAsync(target.SendTarget)).Value.Data.RequiresDuplicateDetection == true
                : (await target.Namespace.Resource.GetServiceBusTopicAsync(target.SendTarget)).Value.Data.RequiresDuplicateDetection == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Copies a received message for resending. With <paramref name="newMessageId"/>, the copy gets a fresh ID and records the
    /// original in <see cref="OriginalMessageIdProperty"/> (the first original is kept if it was already copied before), so
    /// duplicate detection on the target can't discard it.
    /// </summary>
    public static ServiceBusMessage Copy(ServiceBusReceivedMessage message, bool newMessageId)
    {
        var copy = new ServiceBusMessage(message);
        if (!newMessageId) return copy;
        if (!string.IsNullOrEmpty(message.MessageId)) copy.ApplicationProperties.TryAdd(OriginalMessageIdProperty, message.MessageId);
        copy.MessageId = Guid.NewGuid().ToString();
        return copy;
    }

    /// <summary>Sends new messages in batches of up to 100.</summary>
    public async Task SendManyAsync(QueueRef target, IEnumerable<ServiceBusMessage> messages)
    {
        await using var sender = Client(target).CreateSender(target.SendTarget);
        foreach (var chunk in messages.Chunk(100))
            await sender.SendMessagesAsync(chunk);
    }

    public async Task SendAsync(QueueRef q, ServiceBusMessage message)
    {
        await using var sender = Client(q).CreateSender(q.SendTarget);
        await sender.SendMessageAsync(message);
    }

    /// <summary>Schedules a message for future delivery and returns its sequence number.</summary>
    public async Task<long> ScheduleAsync(QueueRef q, ServiceBusMessage message, DateTimeOffset enqueueAt)
    {
        await using var sender = Client(q).CreateSender(q.SendTarget);
        return await sender.ScheduleMessageAsync(message, enqueueAt);
    }

    /// <summary>
    /// Moves the given DLQ messages (by sequence number) to <paramref name="target"/> (default: the entity's own send target).
    /// The target may be on another namespace. Returns the sequence numbers moved.
    /// </summary>
    public async Task<HashSet<long>> MoveFromDeadLetterAsync(QueueRef q, IReadOnlySet<long> sequenceNumbers, QueueRef? target = null) =>
        (await MoveFromDeadLetterWithDetailsAsync(q, sequenceNumbers, target)).Moved;

    /// <summary>
    /// Like <see cref="MoveFromDeadLetterAsync"/>, also reporting whether copies got new message IDs. When the target uses duplicate
    /// detection, a copy that kept its ID would be accepted and then silently discarded as a duplicate of the original send, and the
    /// original would still be completed: the message would be lost. New IDs make the copy a distinct message.
    /// </summary>
    public async Task<(HashSet<long> Moved, bool NewMessageIds)> MoveFromDeadLetterWithDetailsAsync(QueueRef q, IReadOnlySet<long> sequenceNumbers,
        QueueRef? target = null)
    {
        target ??= q;
        var newIds = await RequiresDuplicateDetectionAsync(target);
        await using var sender = Client(target).CreateSender(target.SendTarget);
        // send before complete: worst case a duplicate, never a loss
        var moved = await ProcessDeadLetterAsync(q, sequenceNumbers, m => sender.SendMessageAsync(Copy(m, newIds)));
        return (moved, newIds);
    }

    /// <summary>Removes the given DLQ messages (by sequence number) without resending them. Returns the sequence numbers removed.</summary>
    public Task<HashSet<long>> CompleteFromDeadLetterAsync(QueueRef q, IReadOnlySet<long> sequenceNumbers) =>
        ProcessDeadLetterAsync(q, sequenceNumbers, _ => Task.CompletedTask);

    /// <summary>Deletes every dead-lettered message in batches until empty. Reports the running total; throws if cancelled (already deleted stays deleted).</summary>
    public async Task<int> PurgeDeadLetterAsync(QueueRef q, Action<int>? progress = null, CancellationToken ct = default)
    {
        await using var receiver = Receiver(Client(q), q, deadLetter: true, ServiceBusReceiveMode.ReceiveAndDelete);
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await receiver.ReceiveMessagesAsync(100, FirstOrNext(total), ct);
            if (batch.Count == 0) return total;
            total += batch.Count;
            progress?.Invoke(total);
        }
    }

    async Task<HashSet<long>> ProcessDeadLetterAsync(QueueRef q, IReadOnlySet<long> sequenceNumbers, Func<ServiceBusReceivedMessage, Task> beforeComplete)
    {
        var want = sequenceNumbers.ToHashSet();
        var moved = new HashSet<long>();
        var client = Client(q);
        await using var receiver = Receiver(client, q, deadLetter: true);
        var held = new List<ServiceBusReceivedMessage>();
        var seen = new HashSet<long>();
        try
        {
            while (want.Count > 0)
            {
                var batch = await receiver.ReceiveMessagesAsync(100, FirstOrNext(seen.Count));
                // stop once the DLQ is drained or locks expired and we start seeing repeats
                if (batch.Count == 0 || batch.All(m => !seen.Add(m.SequenceNumber))) break;
                foreach (var m in batch)
                {
                    if (want.Remove(m.SequenceNumber))
                    {
                        await beforeComplete(m);
                        await receiver.CompleteMessageAsync(m);
                        moved.Add(m.SequenceNumber);
                    }
                    else held.Add(m);
                }
            }
        }
        finally
        {
            foreach (var m in held)
                try { await receiver.AbandonMessageAsync(m); } catch (ServiceBusException) { /* lock already expired; message is back anyway */ }
        }
        return moved;
    }

    public async Task<MessageCountDetails?> GetCountsAsync(QueueRef q) => q.Kind switch
    {
        EntityKind.Queue => (await q.Namespace.Resource.GetServiceBusQueueAsync(q.Name)).Value.Data.CountDetails,
        EntityKind.Topic => (await q.Namespace.Resource.GetServiceBusTopicAsync(q.Name)).Value.Data.CountDetails,
        _ => (await (await q.Namespace.Resource.GetServiceBusTopicAsync(q.Name)).Value
            .GetServiceBusSubscriptionAsync(q.Subscription!)).Value.Data.CountDetails,
    };
}
