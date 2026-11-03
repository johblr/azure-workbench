using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;
using Dock.Controls.ProportionalStackPanel;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

static class WorkspaceChecks
{
    public static void Run()
    {
        AppBuilder.Configure<AzureWorkbench.App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var messages = new List<string>();
        var workspace = new WorkspaceTabs(messages.Add);
        var window = new Window { Width = 1400, Height = 850, Content = workspace };
        window.Show();
        var closed = 0;
        WorkspaceDocument Document(string key) => new(key, key, key, new TextBox { Text = key }, () =>
        {
            closed++;
            return Task.CompletedTask;
        });
        var first = workspace.Open("tenant-a/resource", () => Document("tenant-a/resource"));
        var second = workspace.Open("tenant-b/resource", () => Document("tenant-b/resource"));
        ((TextBox)first.View).Text = "retained state";
        Check(ReferenceEquals(workspace.Open("TENANT-A/RESOURCE", () => throw new Exception("Duplicate created")), first),
            "resource identity focuses existing tab case-insensitively");
        workspace.Move(first, 1, true);
        Check(workspace.GroupCount == 2 && workspace.Documents.Count() == 2, "split creates side-by-side tab groups");
        PumpJobs();
        var sash = workspace.GetVisualDescendants().OfType<ProportionalStackPanelSplitter>().Single();
        var beforeWidth = second.View.Bounds.Width;
        var center = sash.TranslatePoint(new Point(sash.Bounds.Width / 2, sash.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseMove(center + new Vector(60, 0));
        window.MouseMove(center + new Vector(100, 0));
        window.MouseUp(center + new Vector(100, 0), MouseButton.Left);
        PumpJobs();
        Check(second.View.Bounds.Width > beforeWidth + 50, "side-by-side group divider resizes by dragging");
        Check(((TextBox)first.View).Text == "retained state" && closed == 0, "splitting retains view state without cleanup");
        workspace.Move(first, -1);
        Check(workspace.GroupCount == 1 && closed == 0, "moving back removes empty group without closing view");
        workspace.Move(first, 1, true);
        workspace.Move(second, 1);
        Check(workspace.GroupCount == 1 && workspace.Documents.Count() == 2, "moving the last tab right collapses its old group");
        workspace.MoveActive(-1);
        Check(messages.Last().Contains("No adjacent group"), "moving beyond an edge explains how to split");
        workspace.Select(second);
        Check(((TextBox)first.View).Text == "retained state", "switching tabs retains hidden document state");
        Check(Pump(workspace.CloseDocumentAsync(first)) && closed == 1, "closing cleans up exactly one document");
        workspace.Move(second, 1, true);
        Check(workspace.GroupCount == 1 && messages.Last().Contains("Open another tab"), "splitting a lone tab does not create an empty pane");
        Pump(workspace.CloseAllAsync());
        Check(workspace.GroupCount == 1 && !workspace.Documents.Any() && closed == 2, "close all leaves a reusable empty group");

        first = workspace.Open("drag-a", () => Document("drag-a"));
        second = workspace.Open("drag-b", () => Document("drag-b"));
        var third = workspace.Open("drag-c", () => Document("drag-c"));
        PumpJobs();
        DocumentTabStripItem Tab(WorkspaceDocument resource) =>
            workspace.GetVisualDescendants().OfType<DocumentTabStripItem>()
                .Single(t => t.DataContext is IDockable d && d.Id == resource.Key);
        Point Center(Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        void Drag(Point from, Point to)
        {
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(15, 0), RawInputModifiers.LeftMouseButton);
            PumpJobs();
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            PumpJobs();
            window.MouseMove(to + new Vector(1, 0), RawInputModifiers.LeftMouseButton);
            PumpJobs();
            window.MouseUp(to, MouseButton.Left);
            PumpJobs();
        }
        var firstModel = (IDockable)Tab(first).DataContext!;
        Drag(Center(Tab(first)), Center(Tab(third)));
        Check(((IDock)firstModel.Owner!).VisibleDockables!.IndexOf(firstModel) > 0, "pointer drag reorders real document tabs");
        workspace.Select(second);
        PumpJobs();
        second.View.Focus();
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
        PumpJobs();
        Check(((IDock)((IDockable)Tab(second).DataContext!).Owner!).ActiveDockable != Tab(second).DataContext,
            "Ctrl+Tab switches documents within a group");
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        PumpJobs();
        Check(((IDock)((IDockable)Tab(second).DataContext!).Owner!).ActiveDockable == Tab(second).DataContext,
            "Ctrl+Shift+Tab switches back");
        Drag(Center(Tab(first)), new Point(window.Width - 12, window.Height / 2));
        Check(workspace.GroupCount == 2, "pointer edge-drop creates a new editor group");
        workspace.Move(first, -1);
        PumpJobs();
        workspace.Split(first, DockOperation.Right);
        PumpJobs();
        Drag(Center(Tab(third)), Center(Tab(first)));
        Check(ReferenceEquals(((IDockable)Tab(third).DataContext!).Owner, firstModel.Owner),
            "pointer drag moves a tab into another existing group");
        workspace.Move(first, -1);
        workspace.Move(third, -1);
        PumpJobs();
        workspace.Select(second);
        window.KeyPressQwerty(PhysicalKey.Backslash, RawInputModifiers.Control | RawInputModifiers.Shift);
        PumpJobs();
        Until(() => second.View.Bounds.Height < window.Height * 0.7);
        Check(workspace.GroupCount == 2 && second.View.Bounds.Height < window.Height * 0.7,
            "Ctrl+Shift+backslash creates a vertical editor group");
        workspace.Move(second, -1);
        PumpJobs();
        workspace.Select(first);
        PumpJobs();
        first.View.Focus();
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        PumpJobs();
        Check(!workspace.Documents.Contains(first), "Ctrl+W closes the focused document through guarded cleanup");
        window.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.Control);
        PumpJobs();
        Check(workspace.Documents.Count() == 1, "repeated Ctrl+W retains keyboard focus on the remaining workspace");
        var remaining = workspace.Documents.Single();
        var middle = Center(Tab(remaining));
        window.MouseDown(middle, MouseButton.Middle);
        window.MouseUp(middle, MouseButton.Middle);
        PumpJobs();
        Check(!workspace.Documents.Contains(remaining), "middle-click closes a document through guarded cleanup");
        Pump(workspace.CloseAllAsync());

        var cleanupGate = new TaskCompletionSource();
        var cleanupCalls = 0;
        var delayed = workspace.Open("delayed-close", () => new WorkspaceDocument("delayed-close", "Delayed", "",
            new TextBox(), () => { cleanupCalls++; return cleanupGate.Task; }));
        PumpJobs();
        var delayedModel = (IDockable)Tab(delayed).DataContext!;
        var nativeClosed = 0;
        delayedModel.Factory!.DockableClosed += (_, e) =>
        {
            if (ReferenceEquals(e.Dockable, delayedModel)) nativeClosed++;
        };
        var closing = workspace.CloseDocumentAsync(delayed);
        delayedModel.Factory.CloseDockable(delayedModel);
        Check(delayed.Closing && !closing.IsCompleted && cleanupCalls == 1
            && !Pump(workspace.CloseAllAsync()), "async cleanup cannot run twice or race with workspace reset");
        cleanupGate.SetResult();
        Check(Pump(closing) && nativeClosed == 1 && !workspace.Documents.Any(),
            "async cleanup completes Dock's native close bookkeeping exactly once");
        var failCleanup = true;
        var retry = workspace.Open("retry-close", () => new WorkspaceDocument("retry-close", "Retry", "", new TextBox(),
            () => failCleanup ? Task.FromException(new IOException("cleanup failed")) : Task.CompletedTask));
        Check(!Pump(workspace.CloseDocumentAsync(retry)) && !retry.Closing && workspace.Documents.Contains(retry)
            && messages.Last().Contains("cleanup failed"), "cleanup failures are reported without silently losing the document");
        failCleanup = false;
        Check(Pump(workspace.CloseDocumentAsync(retry)), "failed tab cleanup can be retried");

        var credentialA = new TestCredential();
        var credentialB = new TestCredential();
        using var armHttp = new HttpClient(new NamespaceHandler());
        var options = new ArmClientOptions { Transport = new HttpClientTransport(armHttp) };
        NamespaceRef Namespace(string subscription, string name, TokenCredential credential)
        {
            var client = new ArmClient(credential, subscription, options);
            var id = new ResourceIdentifier($"/subscriptions/{subscription}/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/{name}");
            return new NamespaceRef(credential, Pump(client.GetServiceBusNamespaceResource(id).GetAsync()).Value);
        }
        var queueA = new QueueRef(Namespace("11111111-1111-1111-1111-111111111111", "namespace-a", credentialA), "orders");
        var queueB = new QueueRef(Namespace("22222222-2222-2222-2222-222222222222", "namespace-b", credentialB), "orders");
        var queues = new QueueService();
        var viewA = new QueueView(queueA, queues, (_, _) => { });
        var viewB = new QueueView(queueB, queues, (_, _) => { });
        var a = workspace.Open("queue-a", () => new WorkspaceDocument("queue-a", "Orders A", queueA.Namespace.Resource.Id.ToString(), viewA, viewA.CloseAsync));
        var b = workspace.Open("queue-b", () => new WorkspaceDocument("queue-b", "Orders B", queueB.Namespace.Resource.Id.ToString(), viewB, viewB.CloseAsync));
        workspace.Move(b, 1, true);
        Check(viewA.Queue == queueA && viewB.Queue == queueB
            && ReferenceEquals(viewA.Queue.Namespace.Credential, credentialA)
            && ReferenceEquals(viewB.Queue.Namespace.Credential, credentialB),
            "same-named queue tabs keep distinct resources and tenant credentials");
        PumpJobs();
        var activeA = Field<RadioButton>(viewA, "_active");
        var dlqA = Field<RadioButton>(viewA, "_deadLetter");
        var activeB = Field<RadioButton>(viewB, "_active");
        dlqA.IsChecked = true;
        Check(activeA.IsChecked == false && activeB.IsChecked == true,
            "active/dead-letter radio choices are isolated per tab");
        Field<NumericUpDown>(viewA, "_count").Value = 50;
        Check(Field<NumericUpDown>(viewB, "_count").Value == 20, "message counts are independent per tab");
        viewA.ShowCounts(ArmServiceBusModelFactory.MessageCountDetails(activeMessageCount: 7, deadLetterMessageCount: 2));
        Check(Field<TextBlock>(viewA, "_counts").Text!.Contains("7 active")
            && string.IsNullOrEmpty(Field<TextBlock>(viewB, "_counts").Text), "queue count updates don't leak between tabs");

        workspace.Select(a);
        var fakeClient = new SendingClient();
        Field<Dictionary<string, ServiceBusClient>>(queues, "_clients").Add(queueA.Namespace.Host, fakeClient);
        var composer = Field<Expander>(viewA, "_composer");
        composer.IsExpanded = true;
        var body = Field<TextBox>(viewA, "_body");
        body.Text = """{"hello":"world"}""";
        Field<TextBox>(viewA, "_subject").Text = "manual event";
        Field<TextBox>(viewA, "_properties").Text = """{"source":"test"}""";
        PumpJobs();
        var send = composer.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Send message"));
        var sendPoint = Center(send);
        Check(sendPoint.Y > 0 && sendPoint.Y < window.Height && send.Bounds.Height > 0,
            "composer send button remains visible in a split editor");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(viewA.IsBusy && Field<ProgressBar>(viewA, "_progress").IsVisible
            && !send.IsEffectivelyEnabled && !body.IsEffectivelyEnabled
            && !activeA.IsEffectivelyEnabled && !Field<NumericUpDown>(viewA, "_count").IsEffectivelyEnabled
            && Field<DockPanel>(viewB, "_work").IsEnabled,
            "sending shows progress and disables conflicting controls in only its own tab");
        Check(Field<TextBlock>(viewA, "_status").Text == "Sending message...", "sending shows a specific operation label");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(fakeClient.Sender.Messages.Count == 1 && fakeClient.QueueName == queueA.Name
            && fakeClient.Sender.Messages[0].Subject == "manual event"
            && fakeClient.Sender.Messages[0].Body.ToString() == body.Text,
            "double-click cannot send twice and DLQ selection still sends to the fixed active queue");
        Check(!Pump(workspace.CloseDocumentAsync(a)), "in-flight sends block tab closure");
        fakeClient.Sender.Gate.SetResult();
        Until(() => !viewA.IsBusy);
        Check(string.IsNullOrEmpty(body.Text) && !composer.IsExpanded
            && !Field<ProgressBar>(viewA, "_progress").IsVisible && Field<DockPanel>(viewA, "_work").IsEnabled
            && Field<TextBlock>(viewA, "_status").Text!.Contains("Sent one message"),
            "confirmed sends clear the body and restore controls and progress");
        composer.IsExpanded = true;
        body.Text = """{"keep":"on failure"}""";
        fakeClient.Sender.Gate = new TaskCompletionSource();
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        fakeClient.Sender.Gate.SetException(new IOException("test send failure"));
        Until(() => !viewA.IsBusy);
        Check(body.Text == """{"keep":"on failure"}""" && composer.IsExpanded
            && Field<TextBlock>(viewA, "_status").Text!.Contains("Delivery may have occurred")
            && Field<DockPanel>(viewA, "_work").IsEnabled, "failed sends keep the draft, report uncertainty and restore controls");
        body.Text = "{invalid";
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !viewA.IsBusy);
        Check(fakeClient.Sender.Messages.Count == 2 && body.Text == "{invalid",
            "invalid drafts show errors without making a send request");
        body.Text = """{"retained":"across docking"}""";
        workspace.Move(a, 1);
        workspace.Select(b);
        workspace.Select(a);
        Check(body.Text == """{"retained":"across docking"}""", "unsent drafts survive tab switches and docking moves");
        workspace.Move(b, 1, true);

        var gateA = new TaskCompletionSource<string>();
        var gateB = new TaskCompletionSource<string>();
        StartOperation(viewA, () => gateA.Task);
        Check(viewA.IsBusy && !viewB.IsBusy, "busy queue does not block another queue tab");
        StartOperation(viewB, () => gateB.Task);
        Check(viewA.IsBusy && viewB.IsBusy, "independent queue operations may run concurrently");
        Check(!Pump(workspace.CloseDocumentAsync(a)) && !Pump(workspace.CloseAllAsync()),
            "busy tabs prevent destructive close and credential reset");
        var busyModel = (IDockable)Tab(a).DataContext!;
        busyModel.Factory!.CloseAllDockables(busyModel);
        Check(workspace.Documents.Contains(a) && workspace.Documents.Contains(b),
            "native context-menu close-all cannot bypass busy tab guards");
        workspace.Move(a, 1);
        Check(viewA.Queue == queueA && viewA.IsBusy, "moving a busy queue tab preserves its operation and resource");
        gateA.SetResult("A complete");
        gateB.SetResult("B complete");
        Until(() => !viewA.IsBusy && !viewB.IsBusy);
        StartOperation(viewA, () => Task.FromException<string>(new IOException("operation failed")));
        Until(() => !viewA.IsBusy);
        Check(!Field<ProgressBar>(viewA, "_progress").IsVisible && Field<DockPanel>(viewA, "_work").IsEnabled
            && Field<TextBlock>(viewA, "_status").Text!.Contains("operation failed"),
            "any failed queue operation restores controls and hides progress");
        typeof(QueueView).GetProperty(nameof(QueueView.HasReceivedMessages))!.SetValue(viewA, true);
        Check(!Pump(workspace.CloseDocumentAsync(a)) && workspace.Documents.Contains(a),
            "received-and-deleted message tabs require explicit discard");
        Check(!Pump(workspace.CloseAllAsync()), "bulk close also guards held received messages");
        workspace.Select(a);
        PumpJobs();
        var heldModel = (IDockable)Tab(a).DataContext!;
        heldModel.Factory!.CloseDockable(heldModel);
        Check(workspace.Documents.Contains(a) && viewA.HasReceivedMessages && !a.Closing,
            "native tab close prompts without discarding received messages");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Check(Pump(workspace.CloseDocumentAsync(a, true)), "explicit discard permits closing held message tab");
        Pump(workspace.CloseAllAsync());
        Pump(queues.DisposeAsync().AsTask());

        using var logHandler = new LogHandler();
        using var logHttp = new HttpClient(logHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var logService = new AppLogService(logHttp);
        var appA = new WebAppRef("/subscriptions/sub-a/resourceGroups/rg/providers/Microsoft.Web/sites/app-a",
            "app-a", "app", credentialA, null);
        var appB = appA with { Id = appA.Id.Replace("sub-a", "sub-b").Replace("app-a", "app-b"), Name = "app-b", Credential = credentialB };
        var logsA = new AppLogsView(logService);
        var logsB = new AppLogsView(logService);
        logHandler.InsightsGate = new TaskCompletionSource<HttpResponseMessage>();
        var selecting = logsA.SelectAsync(appA);
        Check(Field<ProgressBar>(logsA, "_progress").IsVisible && !Field<Button>(logsA, "_reload").IsEnabled
            && !Field<Button>(logsA, "_load").IsEnabled, "Insights discovery shows progress and blocks conflicting queries");
        logHandler.InsightsGate.SetResult(Json("""{"value":[]}"""));
        Pump(selecting);
        logHandler.InsightsGate = null;
        Check(!Field<ProgressBar>(logsA, "_progress").IsVisible && Field<Button>(logsA, "_load").IsEnabled,
            "Insights discovery restores activity controls when complete");
        var insights = new InsightsRef("/subscriptions/sub-a/resourceGroups/rg/providers/microsoft.insights/components/test", "test", "app-id");
        Field<ComboBox>(logsA, "_insights").ItemsSource = new[] { insights };
        Field<ComboBox>(logsA, "_insights").SelectedItem = insights;
        logHandler.QueryGate = new TaskCompletionSource<HttpResponseMessage>();
        Field<Button>(logsA, "_load").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => logHandler.QueryRequests == 1);
        Check(Field<ProgressBar>(logsA, "_progress").IsVisible && !Field<Button>(logsA, "_reload").IsEnabled
            && !Field<ComboBox>(logsA, "_insights").IsEnabled && Field<Button>(logsA, "_cancelQuery").IsEnabled,
            "log queries disable conflicting inputs but keep cancellation available");
        Field<Button>(logsA, "_load").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(logHandler.QueryRequests == 1, "duplicate query clicks cannot start overlapping requests");
        Field<Button>(logsA, "_cancelQuery").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !Field<ProgressBar>(logsA, "_progress").IsVisible);
        Check(Field<Button>(logsA, "_load").IsEnabled && !Field<Button>(logsA, "_cancelQuery").IsEnabled,
            "cancelled log queries restore controls and hide activity feedback");
        logHandler.QueryGate = null;
        Pump(logsB.SelectAsync(appB));
        var docA = workspace.Open("logs-a", () => new WorkspaceDocument("logs-a", "App A logs", appA.Id, logsA, logsA.CloseAsync));
        var docB = workspace.Open("logs-b", () => new WorkspaceDocument("logs-b", "App B logs", appB.Id, logsB, logsB.CloseAsync));
        StartTail(logsA);
        StartTail(logsB);
        Until(() => logHandler.Streams.Count == 2 && logHandler.Streams.All(s => s.Started));
        workspace.Select(docA);
        workspace.Move(docA, 1, true);
        Flush(logsA);
        Flush(logsB);
        Check(logHandler.Streams.All(s => !s.Cancelled), "two streams continue across tab switches and split moves");
        Check(Field<ProgressBar>(logsA, "_progress").IsVisible && Field<Button>(logsA, "_stop").IsEnabled
            && !Field<Button>(logsA, "_start").IsEnabled && Field<Button>(logsA, "_load").IsEnabled,
            "live streaming shows activity with Stop available and recent queries independent");
        PumpJobs();
        Check(Field<SelectableTextBlock>(logsA, "_live").Text!.Contains("app-a")
            && Field<SelectableTextBlock>(logsB, "_live").Text!.Contains("app-b"),
            "live output stays in its own tenant/app tab");
        var liveB = Field<SelectableTextBlock>(logsB, "_live");
        void Feed(AppLogsView view, string text)
        {
            Field<LogBuffer>(view, "_buffer").Append(text);
            view.GetType().GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, true);
        }
        Feed(logsB, "info: ready\nfail: payment declined\n   at Pay()\n");
        Field<ComboBox>(logsB, "_filterLevel").SelectedIndex = 3;
        PumpJobs();
        Check(liveB.Text!.Contains("payment declined") && liveB.Text.Contains("at Pay()") && !liveB.Text.Contains("ready")
            && Field<TextBlock>(logsB, "_filterInfo").Text != null, "live tail display filters by level");
        Field<TextBox>(logsB, "_filterText").Text = "nomatch";
        PumpJobs();
        Check(!liveB.Text!.Contains("payment"), "live tail display filters by text");
        Field<TextBox>(logsB, "_filterText").Text = "";
        Field<ComboBox>(logsB, "_filterLevel").SelectedIndex = 0;
        PumpJobs();
        liveB.SelectionStart = 0;
        liveB.SelectionEnd = 4;
        var held = liveB.Text;
        Feed(logsB, "info: newer line\n");
        Flush(logsB);
        Check(liveB.Text == held && liveB.SelectionEnd == 4, "live display pauses while text is selected so it can be copied");
        liveB.SelectionEnd = 0;
        Flush(logsB);
        Check(liveB.Text!.Contains("newer line"), "live display resumes once the selection is cleared");
        logsB.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(logsB, new List<LogEntry>
        {
            new("t1", "1", "orders", "checkout ok"), new("t2", "3", "orders", "checkout failed"),
        });
        Field<TextBox>(logsB, "_filterText").Text = "checkout";
        Field<ComboBox>(logsB, "_filterLevel").SelectedIndex = 3;
        PumpJobs();
        var recentB = Field<SelectableTextBlock>(logsB, "_recent").Text!;
        Check(recentB.Contains("checkout failed") && !recentB.Contains("checkout ok"), "recent results filter by level and text without re-querying");
        Field<ComboBox>(logsB, "_filterLevel").SelectedIndex = 0;
        Field<TextBox>(logsB, "_filterText").Text = "";
        workspace.Select(docB);
        PumpJobs();
        Field<CheckBox>(logsB, "_filterRegex").Focus();
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        PumpJobs();
        Check(Field<TextBox>(logsB, "_filterText").IsFocused, "Ctrl+F focuses the log filter");
        Pump(workspace.CloseDocumentAsync(docA));
        Check(logHandler.Streams.Count(s => s.Cancelled) == 1 && workspace.Documents.Contains(docB),
            "closing one log tab cancels only its own idle stream");
        Pump(workspace.CloseAllAsync());
        Check(logHandler.Streams.All(s => s.Cancelled), "closing workspace cancels every remaining stream");
        window.Close();

        // The real shell must still load its icon and close cleanly without signing in.
        var shell = new MainWindow();
        shell.Show();
        PumpJobs();
        var shellGate = new TaskCompletionSource<string>();
        typeof(MainWindow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(shell, new object[] { (Func<Task<string>>)(() => shellGate.Task) });
        Check(Field<ProgressBar>(shell, "_progress").IsVisible && !Field<TreeView>(shell, "_tree").IsEnabled
            && !Field<StackPanel>(shell, "_signIn").IsEnabled, "shell operations show progress and disable conflicting navigation");
        shellGate.SetResult("Shell operation done");
        Until(() => !Field<ProgressBar>(shell, "_progress").IsVisible);
        Check(Field<TreeView>(shell, "_tree").IsEnabled && Field<StackPanel>(shell, "_signIn").IsEnabled,
            "shell operation completion restores navigation controls");
        shell.Close();
        Until(() => !shell.IsVisible);
        Console.WriteLine("Workspace checks passed.");
    }

    static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void StartOperation(QueueView view, Func<Task<string>> operation) =>
        typeof(QueueView).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [operation]);

    static void StartTail(AppLogsView view)
    {
        var tabs = view.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = 1;
        PumpJobs();
        Field<Button>(view, "_start").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    static void Flush(AppLogsView view) =>
        typeof(AppLogsView).GetMethod("Flush", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [false]);

    static T Pump<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Workspace check timed out");
            Thread.Sleep(5);
        }
        PumpJobs();
    }

    sealed class NamespaceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = request.RequestUri!.AbsolutePath;
            var name = id.Split('/').Last();
            if (id.Contains("/queues/"))
                return Task.FromResult(Json($$$$"""
                    {"id":"{{{{id}}}}","name":"{{{{name}}}}","type":"Microsoft.ServiceBus/namespaces/queues",
                    "properties":{"countDetails":{"activeMessageCount":8,"deadLetterMessageCount":2}}}
                    """));
            return Task.FromResult(Json($$$"""
                {"id":"{{{id}}}","name":"{{{name}}}","type":"Microsoft.ServiceBus/namespaces",
                "location":"westeurope","properties":{"serviceBusEndpoint":"https://{{{name}}}.servicebus.windows.net/"}}
                """));
        }

    }

    sealed class SendingClient : ServiceBusClient
    {
        public SendingSender Sender { get; } = new();
        public string? QueueName { get; private set; }
        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            QueueName = queueOrTopicName;
            return Sender;
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class SendingSender : ServiceBusSender
    {
        public List<ServiceBusMessage> Messages { get; } = [];
        public TaskCompletionSource Gate { get; set; } = new();
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Gate.Task;
        }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json"),
    };

    sealed class LogHandler : HttpMessageHandler
    {
        public List<LiveStream> Streams { get; } = [];
        public TaskCompletionSource<HttpResponseMessage>? InsightsGate { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? QueryGate { get; set; }
        public int QueryRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/components"))
                return InsightsGate?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Json("""{"value":[]}"""));
            if (uri.Host == "api.applicationinsights.io")
            {
                QueryRequests++;
                return QueryGate?.Task.WaitAsync(cancellationToken) ?? throw new InvalidOperationException("Unexpected query");
            }
            if (uri.Host == "management.azure.com")
            {
                var name = uri.AbsolutePath.Split('/').Last();
                return Task.FromResult(Json($$$"""
                    {"properties":{"hostNameSslStates":[{"hostType":"Repository","name":"{{{name}}}.scm.azurewebsites.net"}]}}
                    """));
            }
            var stream = new LiveStream(uri.Host);
            Streams.Add(stream);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    sealed class LiveStream(string host) : Stream
    {
        public volatile bool Started;
        public volatile bool Cancelled;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!Started)
            {
                var text = Encoding.UTF8.GetBytes(host + " live line\n");
                text.CopyTo(buffer);
                Started = true;
                return text.Length;
            }
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
