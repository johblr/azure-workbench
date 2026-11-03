using Azure.Messaging.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using AzureWorkbench.Models;

namespace AzureWorkbench.Services;

/// <summary>Service Bus data-plane operations. Keeps one AMQP connection per namespace, since opening one costs about a second.</summary>
public sealed class QueueService : IAsyncDisposable
{
    // Cache lookup runs on the UI thread before awaiting; SDK clients support concurrent tab operations.
    readonly Dictionary<string, ServiceBusClient> _clients = new();

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

    /// <summary>Sends copies of the messages to <paramref name="target"/> (its send target; on its own namespace connection).</summary>
    public Task ResendAsync(QueueRef target, IEnumerable<ServiceBusReceivedMessage> messages) =>
        SendManyAsync(target, messages.Select(m => new ServiceBusMessage(m)));

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
    public async Task<HashSet<long>> MoveFromDeadLetterAsync(QueueRef q, IReadOnlySet<long> sequenceNumbers, QueueRef? target = null)
    {
        await using var sender = Client(target ?? q).CreateSender((target ?? q).SendTarget);
        // send before complete: worst case a duplicate, never a loss
        return await ProcessDeadLetterAsync(q, sequenceNumbers, m => sender.SendMessageAsync(new ServiceBusMessage(m)));
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
