using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

/// <summary>Service Bus tab: pure archive/trace/filter logic and view behaviour against fake clients.</summary>
static class ServiceBusChecks
{
    static int _checks;

    public static void Run()
    {
        PureChecks();
        ViewChecks();
        Console.WriteLine($"Service Bus checks passed ({_checks}).");
    }

    // ------------------------------------------------------------------ pure logic

    static ServiceBusReceivedMessage Msg(long seq, string id, string body = "{}", string? subject = null, string? correlation = null,
        string? reason = null, string? description = null, IDictionary<string, object>? props = null)
    {
        var all = new Dictionary<string, object>(props ?? new Dictionary<string, object>());
        if (reason != null) all["DeadLetterReason"] = reason;
        if (description != null) all["DeadLetterErrorDescription"] = description;
        return ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString(body), messageId: id, correlationId: correlation,
            subject: subject, contentType: "application/json", properties: all, sequenceNumber: seq,
            enqueuedTime: new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero).AddMinutes(seq));
    }

    static void PureChecks()
    {
        var original = Msg(7, "id-1", "{\"name\":\"Zoë 日本語 \\\"quoted\\\" 🚀\"}", "Order Créé", "corr-1", "MaxDelivery", "too many",
            new Dictionary<string, object>
            {
                ["s"] = "ünï", ["b"] = true, ["i"] = 42, ["l"] = 9_000_000_000L, ["d"] = 1.5, ["guid"] = Guid.NewGuid(),
            });
        var json = MessageArchive.Serialize([original]);
        var parsed = MessageArchive.Parse(json);
        var a = parsed.Single();
        Check(a.MessageId == "id-1" && a.Subject == "Order Créé" && a.CorrelationId == "corr-1" && a.ContentType == "application/json"
            && a.Body == original.Body.ToString() && a.SequenceNumber == 7 && a.DeadLetterReason == "MaxDelivery"
            && a.DeadLetterErrorDescription == "too many" && a.EnqueuedTime == original.EnqueuedTime,
            "archive round trip keeps metadata, unicode body and dead-letter fields");
        Check(a.ApplicationProperties["s"] is "ünï" && a.ApplicationProperties["b"] is true && a.ApplicationProperties["i"] is 42L
            && a.ApplicationProperties["l"] is 9_000_000_000L && a.ApplicationProperties["d"] is 1.5d,
            "archive round trip keeps string, bool and number properties");
        Check(!a.ApplicationProperties.ContainsKey("guid") && !a.ApplicationProperties.ContainsKey("DeadLetterReason"),
            "non-scalar properties and dead-letter markers are not archived as properties");
        Check(json.Contains("日本語") && json.Contains("Zoë"), "archive keeps readable unicode");
        Check(MessageArchive.Parse(MessageArchive.Serialize([])).Count == 0, "empty archive round trips");
        Check(MessageArchive.Parse("[" + MessageArchive.SerializeOne(original) + "]").Count == 1, "single message JSON is a valid archive item");
        Throws<FormatException>(() => MessageArchive.Parse("{}"), "archive must be an array");
        Throws<FormatException>(() => MessageArchive.Parse("[{\"subject\":\"x\"}]"), "archive items need a body");
        Throws<FormatException>(() => MessageArchive.Parse("not json"), "archive rejects malformed JSON");
        var resend = MessageArchive.ToMessage(a);
        Check(resend.MessageId == "id-1" && resend.Body.ToString() == a.Body && resend.ApplicationProperties["l"] is 9_000_000_000L,
            "imported messages keep the original message ID and body");
        Check(!string.IsNullOrEmpty(MessageArchive.ToMessage(a with { MessageId = null }).MessageId), "missing message IDs are generated");
        var propsJson = MessageArchive.PropertiesToJson(original.ApplicationProperties);
        Check(propsJson.Contains("\"s\": \"ünï\"") && !propsJson.Contains("guid") && MessageArchive.PropertiesToJson([]) == "",
            "properties convert to a JSON object for the composer");

        const string trace = "4bf92f3577b34da6a3ce929d0e0e4736";
        Check(MessageFormatter.TraceId(new Dictionary<string, object> { ["traceparent"] = $"00-{trace}-00f067aa0ba902b7-01" }, "c") == trace,
            "trace id comes from traceparent");
        Check(MessageFormatter.TraceId(new Dictionary<string, object> { ["Diagnostic-Id"] = $"00-{trace.ToUpperInvariant()}-00f067aa0ba902b7-00" }, null) == trace,
            "trace id comes from W3C Diagnostic-Id, lower-cased");
        Check(MessageFormatter.TraceId(new Dictionary<string, object> { ["Diagnostic-Id"] = "|abc123.1." }, "corr-9") == "corr-9",
            "legacy Diagnostic-Id falls back to the correlation id");
        Check(MessageFormatter.TraceId(new Dictionary<string, object>(), "corr-9") == "corr-9"
            && MessageFormatter.TraceId(new Dictionary<string, object>(), " ") == null, "correlation id is the fallback, nothing otherwise");
        Check(MessageFormatter.TraceId(new Dictionary<string, object> { ["traceparent"] = $"00-{new string('0', 32)}-00f067aa0ba902b7-01" }, null) == null,
            "an all-zero trace id is ignored");

        var timeout = Msg(1, "m-100", "{\"customer\":\"Contoso\"}", "OrderFailed", "corr-A", "Timeout", "Handler took too long",
            new Dictionary<string, object> { ["Region"] = "westeurope", ["Retry"] = 3 });
        bool Matches(string text, bool regex = false, string? reason = null) => MessageFormatter.Matches(timeout, new LogFilter(0, text, regex), reason);
        Check(Matches("") && Matches("m-100") && Matches("orderfailed") && Matches("CORR-a") && Matches("timeout")
            && Matches("took too long") && Matches("contoso") && Matches("region") && Matches("WESTEUROPE") && Matches("retry=3"),
            "filter matches id, subject, correlation, reason, description, body and properties case-insensitively");
        Check(!Matches("nomatch") && Matches(@"m-\d{3}", true) && !Matches(@"^zzz", true) && Matches("x", false, null) == false,
            "filter supports regex and rejects non-matches");
        Check(Matches("", false, "Timeout") && !Matches("", false, "Poison"), "reason filter requires an exact dead-letter reason");
    }

    // ------------------------------------------------------------------ fakes

    sealed class FakeClient(string host, List<string> log) : ServiceBusClient
    {
        readonly Dictionary<string, FakeSender> _senders = new();
        public FakeReceiver Active { get; } = new(log);
        public FakeReceiver Dlq { get; } = new(log);
        public FakeSender Sender(string name) =>
            _senders.TryGetValue(name, out var s) ? s : _senders[name] = new FakeSender($"{host}/{name}", log);
        public override ServiceBusSender CreateSender(string queueOrTopicName) => Sender(queueOrTopicName);
        public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options) =>
            options.SubQueue == SubQueue.DeadLetter ? Dlq : Active;
        public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options) =>
            options.SubQueue == SubQueue.DeadLetter ? Dlq : Active;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeSender(string path, List<string> log) : ServiceBusSender
    {
        public List<ServiceBusMessage> Sent { get; } = [];
        public List<(ServiceBusMessage Message, DateTimeOffset At)> Scheduled { get; } = [];
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            log.Add($"send:{path}:{message.MessageId}");
            return Task.CompletedTask;
        }
        public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
        {
            foreach (var m in messages)
            {
                Sent.Add(m);
                log.Add($"send:{path}:{m.MessageId}");
            }
            return Task.CompletedTask;
        }
        public override Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Scheduled.Add((message, scheduledEnqueueTime));
            return Task.FromResult(4242L);
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeReceiver(List<string> log) : ServiceBusReceiver
    {
        public List<ServiceBusReceivedMessage> Messages { get; } = [];
        public List<long?> PeekFrom { get; } = [];
        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
        {
            PeekFrom.Add(fromSequenceNumber);
            IReadOnlyList<ServiceBusReceivedMessage> result = Messages.Where(m => m.SequenceNumber >= (fromSequenceNumber ?? 0)).Take(maxMessages).ToList();
            return Task.FromResult(result);
        }
        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Messages.Take(maxMessages).ToList();
            foreach (var m in batch) Messages.Remove(m);
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch);
        }
        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            log.Add($"complete:{message.SequenceNumber}");
            return Task.CompletedTask;
        }
        public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            log.Add($"abandon:{message.SequenceNumber}");
            return Task.CompletedTask;
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeWorkbench(IReadOnlyList<QueueRef> entities) : IWorkbench
    {
        public List<string> Searched { get; } = [];
        public IReadOnlyList<QueueRef> KnownEntities => entities;
        public IReadOnlyList<AppConfigView> OpenConfigurations => [];
        public void FindInLogs(string operationId, Control anchor) => Searched.Add(operationId);
        public void Notify(string message) { }
    }

    sealed class ArmHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var name = path.Split('/').Last();
            var json = path.Contains("/queues/") || path.Contains("/topics/")
                ? $$$$"""{"id":"{{{{path}}}}","name":"{{{{name}}}}","properties":{"countDetails":{"activeMessageCount":1,"deadLetterMessageCount":2}}}"""
                : $$$"""{"id":"{{{path}}}","name":"{{{name}}}","type":"Microsoft.ServiceBus/namespaces","location":"westeurope","properties":{"serviceBusEndpoint":"https://{{{name}}}.servicebus.windows.net/"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    // ------------------------------------------------------------------ view behaviour

    static void ViewChecks()
    {
        using var http = new HttpClient(new ArmHandler());
        var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
        var credential = new TestCredential();
        NamespaceRef Namespace(string subscription, string name)
        {
            var arm = new ArmClient(credential, subscription, options);
            var id = new ResourceIdentifier($"/subscriptions/{subscription}/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/{name}");
            return new NamespaceRef(credential, Pump(arm.GetServiceBusNamespaceResource(id).GetAsync()).Value);
        }
        var ns1 = Namespace("11111111-1111-1111-1111-111111111111", "sb-one");
        var ns2 = Namespace("22222222-2222-2222-2222-222222222222", "sb-two");
        var log = new List<string>();
        var client1 = new FakeClient(ns1.Host, log);
        var client2 = new FakeClient(ns2.Host, log);
        var service = new QueueService();
        var clients = (Dictionary<string, ServiceBusClient>)typeof(QueueService).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        clients[ns1.Host] = client1;
        clients[ns2.Host] = client2;

        var orders = new QueueRef(ns1, "orders");
        var target = new QueueRef(ns2, "target-q");
        var events = new QueueRef(ns1, "events", IsTopic: true);
        var audit = new QueueRef(ns1, "events", "audit");
        var other = new QueueRef(ns1, "events", "other");
        var workbench = new FakeWorkbench([orders, target, events, audit, other]);

        // topic: composer only
        var topicView = new QueueView(events, service, (_, _) => { }, workbench);
        var window = new Window { Width = 1300, Height = 900 };
        window.Show();
        window.Content = topicView;
        PumpJobs();
        var topicPeek = F<Button>(topicView, "_peek");
        Check(!topicPeek.IsVisible && !topicPeek.IsEnabled && !F<Button>(topicView, "_receive").IsVisible
            && !F<Button>(topicView, "_moveTo").IsVisible && !F<RadioButton>(topicView, "_deadLetter").IsVisible && !F<ListBox>(topicView, "_list").IsEffectivelyVisible,
            "topic tab hides peek, receive, move and message list controls");
        Check(F<Expander>(topicView, "_composer").IsExpanded && F<Button>(topicView, "_refreshCounts").IsVisible
            && Texts(topicView).Any(t => t.StartsWith("Topic · events")), "topic tab shows the expanded composer, counts and a kind title");
        F<TextBox>(topicView, "_body").Text = "{\"t\":1}";
        Click(Composer(topicView, "Send message"));
        Until(() => !topicView.IsBusy);
        Check(client1.Sender("events").Sent.Count == 1 && F<Expander>(topicView, "_composer").IsExpanded,
            "topic sends go to the topic and keep the composer open");

        // subscription: warning and send target
        var subView = new QueueView(audit, service, (_, _) => { }, workbench);
        window.Content = subView;
        PumpJobs();
        Check(Texts(subView).Any(t => t.Contains("Sends from a subscription go to topic events; every subscription whose rules match receives them.")),
            "subscription tab warns that sends go to the topic");
        Check(Texts(subView).Any(t => t.StartsWith("Subscription · events/audit")) && F<Button>(subView, "_peek").IsVisible, "subscription tab is titled by kind and keeps receive controls");
        var targets = ((System.Collections.IEnumerable)typeof(QueueView).GetMethod("Targets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(subView, null)!)
            .Cast<object>().Select(t => (string)t.GetType().GetProperty("Label")!.GetValue(t)!).ToList();
        Check(targets.Count == 2 && targets[0].StartsWith("This entity · topic · events") && targets[1].StartsWith("queue · orders")
            || targets.Count == 3 && targets[0].StartsWith("This entity · topic · events"),
            "subscription targets start with this entity (the topic) and de-duplicate the topic");
        var queueView0 = new QueueView(orders, service, (_, _) => { }, workbench);
        var queueTargets = ((System.Collections.IEnumerable)typeof(QueueView).GetMethod("Targets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(queueView0, null)!)
            .Cast<object>().Select(t => (string)t.GetType().GetProperty("Label")!.GetValue(t)!).ToList();
        Check(queueTargets.SequenceEqual(["This entity · queue · orders (sb-one)", "queue · target-q (sb-two)", "topic · events (sb-one)"]),
            "target list is this entity first, then queues and topics once each, labelled with the namespace");

        // queue DLQ: peek, filter, selection
        var view = new QueueView(orders, service, (_, _) => { }, workbench);
        window.Content = view;
        PumpJobs();
        client1.Dlq.Messages.AddRange([
            Msg(1, "m1", "{\"customer\":\"Contoso\"}", "OrderFailed", "corr-1", "Timeout", "slow",
                new Dictionary<string, object> { ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", ["Region"] = "eu" }),
            Msg(2, "m2", "{\"customer\":\"Fabrikam\"}", "OrderFailed", "corr-2", "Poison", "bad json"),
            Msg(3, "m3", "{\"customer\":\"Contoso\"}", "OrderShipped", null, "Timeout", "slow"),
            Msg(4, "m4", "plain", "Other", "corr-4", "Poison", "bad"),
            Msg(5, "m5", "plain", "Other", "corr-5", "Poison", "bad"),
        ]);
        F<RadioButton>(view, "_deadLetter").IsChecked = true;
        F<NumericUpDown>(view, "_count").Value = 2;
        Click(F<Button>(view, "_peek"));
        Until(() => !view.IsBusy);
        var shown = F<System.Collections.ObjectModel.ObservableCollection<MessageItem>>(view, "_shown");
        var list = F<ListBox>(view, "_list");
        var peekNext = F<Button>(view, "_peekNext");
        Check(shown.Count == 2 && peekNext.IsEnabled && !F<Button>(view, "_editAsNew").IsEnabled && !F<Button>(view, "_moveTo").IsEnabled,
            "peek lists messages, enables Peek next and keeps selection actions disabled until selected");
        Click(peekNext);
        Until(() => !view.IsBusy);
        Check(shown.Count == 4 && client1.Dlq.PeekFrom[^1] == 3 && peekNext.IsEnabled,
            "Peek next continues from the last sequence number + 1 and appends");
        Click(peekNext);
        Until(() => !view.IsBusy);
        Check(shown.Count == 5 && client1.Dlq.PeekFrom.Contains(5) && !peekNext.IsEnabled,
            "Peek next is disabled once a peek returns fewer than requested");

        var reasons = F<ComboBox>(view, "_reason");
        Check(reasons.ItemsSource!.Cast<string>().SequenceEqual(["Any reason", "Poison", "Timeout"]) && reasons.IsVisible,
            "reason picker lists distinct dead-letter reasons plus Any reason");
        Click(F<Button>(view, "_selectAll"));
        Check(list.SelectedItems!.Count == 5 && F<Button>(view, "_moveTo").IsEnabled && F<Button>(view, "_complete").IsEnabled
            && !F<Button>(view, "_editAsNew").IsEnabled, "Select all shown selects every shown message and enables bulk actions");
        var filter = F<TextBox>(view, "_filter");
        filter.Text = "contoso";
        Check(shown.Count == 2 && list.SelectedItems.Count == 2 && F<TextBlock>(view, "_showing").Text == "showing 2 of 5"
            && list.SelectedItems.Cast<MessageItem>().All(m => shown.Contains(m)),
            "filter hides non-matching messages, deselects them and reports counts");
        filter.Text = "";
        Check(shown.Count == 5 && list.SelectedItems.Count == 2, "clearing the filter restores rows without re-selecting hidden ones");
        reasons.SelectedItem = "Poison";
        Check(shown.Count == 3 && list.SelectedItems.Cast<MessageItem>().All(m => m.Message.DeadLetterReason == "Poison"),
            "reason picker filters and deselects other reasons");
        reasons.SelectedItem = "Any reason";
        filter.IsEnabled = true;
        F<CheckBox>(view, "_regex").IsChecked = true;
        filter.Text = @"m[34]|OrderFailed";
        Check(shown.Count == 4, "regex filter matches across fields");
        filter.Text = "([";
        Check(shown.Count == 4 && F<TextBlock>(view, "_showing").Text!.StartsWith("Invalid regex"), "invalid regex is reported and leaves the list unchanged");
        F<CheckBox>(view, "_regex").IsChecked = false;
        filter.Text = "";
        F<Button>(view, "_peek").Focus();
        PumpJobs();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        PumpJobs();
        Check(filter.IsFocused, "Ctrl+F focuses the filter");
        filter.Text = "abc";
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Check(filter.Text == "", "Esc clears the filter");

        // details, find in logs, copy buttons
        list.SelectedItems.Clear();
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m1");
        var find = F<Button>(view, "_findLogs");
        Check(find.IsVisible, "Find in logs appears for a message with a trace id");
        Click(find);
        Check(workbench.Searched.SequenceEqual(["4bf92f3577b34da6a3ce929d0e0e4736"]), "Find in logs searches the 32-hex trace id");
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m4");
        Check(F<Button>(view, "_findLogs").IsVisible, "correlation id alone also offers Find in logs");
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m3");
        Check(!F<Button>(view, "_findLogs").IsVisible, "no trace or correlation id hides Find in logs");

        // edit as new
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m1");
        var editAsNew = F<Button>(view, "_editAsNew");
        Check(editAsNew.IsEnabled, "Edit as new is enabled for exactly one selected message");
        F<Expander>(view, "_composer").IsExpanded = false;
        Click(editAsNew);
        Check(F<TextBox>(view, "_body").Text == "{\"customer\":\"Contoso\"}" && F<TextBox>(view, "_subject").Text == "OrderFailed"
            && F<TextBox>(view, "_contentType").Text == "application/json" && F<TextBox>(view, "_properties").Text!.Contains("\"Region\": \"eu\"")
            && !F<TextBox>(view, "_properties").Text!.Contains("DeadLetter") && F<Expander>(view, "_composer").IsExpanded
            && F<TextBlock>(view, "_status").Text!.Contains("NEW message") && F<TextBlock>(view, "_status").Text!.Contains("Complete selected"),
            "Edit as new copies the message into the composer and explains that a new message is created");
        list.SelectAll();
        Check(!editAsNew.IsEnabled, "Edit as new needs exactly one selection");

        // scheduled send
        var composerBody = F<TextBox>(view, "_body");
        var sendAt = F<TextBox>(view, "_sendAt");
        var future = DateTimeOffset.Now.AddDays(2);
        sendAt.Text = future.ToString("g", CultureInfo.CurrentCulture);
        Click(Composer(view, "Send message"));
        Until(() => !view.IsBusy);
        var scheduled = client1.Sender("orders").Scheduled;
        Check(scheduled.Count == 1 && Math.Abs((scheduled[0].At - future).TotalMinutes) < 1.5 && client1.Sender("orders").Sent.Count == 0
            && F<TextBlock>(view, "_status").Text!.Contains("4242") && F<TextBlock>(view, "_status").Text!.Contains("Scheduled"),
            "a Send at time calls ScheduleMessageAsync and reports the sequence number");
        composerBody.Text = "{\"again\":true}";
        sendAt.Text = DateTimeOffset.Now.AddHours(-1).ToString("g", CultureInfo.CurrentCulture);
        Click(Composer(view, "Send message"));
        Until(() => !view.IsBusy);
        Check(scheduled.Count == 1 && client1.Sender("orders").Sent.Count == 0 && F<TextBlock>(view, "_status").Text!.Contains("future"),
            "a Send at time in the past is refused");
        sendAt.Text = "not a date";
        Click(Composer(view, "Send message"));
        Until(() => !view.IsBusy);
        Check(scheduled.Count == 1 && F<TextBlock>(view, "_status").Text!.Contains("Could not read"), "an unreadable Send at time is refused");
        sendAt.Text = "";
        Click(Composer(view, "Send message"));
        Until(() => !view.IsBusy);
        Check(client1.Sender("orders").Sent.Count == 1, "an empty Send at sends immediately");

        // cross-namespace move: send on the target namespace before completing
        list.SelectedItems.Clear();
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m2");
        log.Clear();
        client1.Dlq.Messages.Clear();
        client1.Dlq.Messages.AddRange(shown.Select(m => m.Message));
        Run(view, "Move", target);
        Until(() => !view.IsBusy);
        Check(client2.Sender("target-q").Sent.Count == 1 && client2.Sender("target-q").Sent[0].MessageId == "m2"
            && client1.Sender("orders").Sent.Count == 1,
            "moving across namespaces sends through the target namespace client");
        var sendAtIndex = log.IndexOf($"send:{ns2.Host}/target-q:m2");
        var completeIndex = log.IndexOf("complete:2");
        Check(sendAtIndex >= 0 && completeIndex > sendAtIndex && !log.Any(l => l == "complete:1"),
            "move sends before completing and completes only the selected message");
        Check(shown.All(m => m.Message.MessageId != "m2") && shown.Count == 4 && log.Count(l => l.StartsWith("abandon:")) == 4,
            "moved messages leave the list and other locked messages are abandoned");

        // complete selected
        list.SelectedItem = shown.First(m => m.Message.MessageId == "m5");
        log.Clear();
        client1.Dlq.Messages.Clear();
        client1.Dlq.Messages.AddRange(shown.Select(m => m.Message));
        var sentBefore = client1.Sender("orders").Sent.Count + client2.Sender("target-q").Sent.Count;
        Run(view, "Complete");
        Until(() => !view.IsBusy);
        Check(log.Contains("complete:5") && !log.Any(l => l.StartsWith("send:")) && shown.All(m => m.Message.MessageId != "m5")
            && client1.Sender("orders").Sent.Count + client2.Sender("target-q").Sent.Count == sentBefore,
            "Complete selected removes the message without sending anything");

        // import send keeps message ids
        var archive = MessageArchive.Parse(MessageArchive.Serialize([Msg(9, "keep-me", "{\"i\":1}", "S", null, null, null, new Dictionary<string, object> { ["k"] = "v" })]));
        client1.Sender("orders").Sent.Clear();
        Run(view, "SendImported", archive);
        Until(() => !view.IsBusy);
        Check(client1.Sender("orders").Sent.Single().MessageId == "keep-me" && F<TextBlock>(view, "_status").Text!.Contains("Sent 1 imported"),
            "imported messages are sent with their original message IDs");

        // purge
        client1.Dlq.Messages.Clear();
        for (var i = 1; i <= 250; i++) client1.Dlq.Messages.Add(Msg(i, $"p{i}"));
        Run(view, "Purge");
        Until(() => !view.IsBusy);
        Check(client1.Dlq.Messages.Count == 0 && F<TextBlock>(view, "_status").Text!.Contains("Purged 250 messages") && shown.Count == 0,
            "purge deletes the dead-letter queue in batches, reports the total and clears peeked rows");
        Check(client1.Active.Messages.Count == 0 && !F<Button>(view, "_cancel").IsVisible, "purge never touches the active queue and hides Cancel afterwards");
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        client1.Dlq.Messages.Add(Msg(1, "x"));
        ThrowsAsync<OperationCanceledException>(() => service.PurgeDeadLetterAsync(orders, null, cancelled.Token), "a cancelled purge stops and throws");
        Check(client1.Dlq.Messages.Count == 1, "a cancelled purge leaves remaining messages");

        // received-and-deleted messages stay guarded and can be resent elsewhere
        client1.Active.Messages.AddRange([Msg(20, "r1"), Msg(21, "r2")]);
        F<RadioButton>(view, "_active").IsChecked = true;
        Run(view, "Receive");
        Until(() => !view.IsBusy);
        Check(view.HasReceivedMessages && shown.Count == 2 && !F<Button>(view, "_peekNext").IsEnabled && !F<Button>(view, "_moveTo").IsEnabled,
            "received-and-deleted messages are held, disable Peek next and Move");
        Run(view, "Peek");
        Until(() => !view.IsBusy);
        Check(view.HasReceivedMessages && F<TextBlock>(view, "_status").Text!.Contains("received-and-deleted"), "peeking again is refused while messages are held");
        list.SelectedItem = shown[0];
        Run(view, "Resend", target);
        Until(() => !view.IsBusy);
        Check(client2.Sender("target-q").Sent.Any(m => m.MessageId == "r1") && shown.Count == 1 && view.HasReceivedMessages,
            "resend to another namespace removes only the resent held message");

        window.Close();
    }

    // ------------------------------------------------------------------ helpers

    static void Run(QueueView view, string method, params object[] args)
    {
        var m = typeof(QueueView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameters = m.GetParameters();
        var full = args.Length == parameters.Length ? args : args.Concat(parameters.Skip(args.Length).Select(_ => (object)null!)).ToArray();
        typeof(QueueView).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [new Func<Task<string>>(() => (Task<string>)m.Invoke(view, full)!)]);
    }

    static Button Composer(QueueView view, string text) =>
        F<Expander>(view, "_composer").GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, text));

    static IEnumerable<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static T F<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException($"FAIL: {name} did not throw {typeof(T).Name}");
    }

    static void ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        var task = action();
        Until(() => task.IsCompleted);
        if (task.IsCanceled && typeof(T) == typeof(OperationCanceledException) || task.Exception?.InnerException is T) Check(true, name);
        else throw new InvalidOperationException($"FAIL: {name} did not throw {typeof(T).Name}");
    }

    static T Pump<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Service Bus check timed out");
            Thread.Sleep(5);
        }
        PumpJobs();
    }
}
