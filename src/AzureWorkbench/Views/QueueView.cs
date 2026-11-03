using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Azure.Messaging.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

namespace AzureWorkbench.Views;

public sealed class QueueView : UserControl, IWorkspaceView
{
    const string Columns = "65,150,2*,*,*";
    const string AnyReason = "Any reason";
    static readonly FilePickerFileType JsonFiles = new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    sealed record Target(QueueRef Ref, string Label);

    readonly QueueService _service;
    readonly Action<QueueRef, MessageCountDetails?> _countsChanged;
    readonly IWorkbench? _workbench;
    readonly TextBlock _counts = new() { Opacity = Ui.Muted, Margin = new Thickness(16, 0, 16, 4), FontSize = 12 };
    readonly TextBlock _status = Ui.StatusLine();
    readonly NumericUpDown _count = new() { Value = 20, Minimum = 1, Maximum = 5000, Increment = 10, Width = 100, FormatString = "0" };
    readonly RadioButton _active = new() { Content = "Active", IsChecked = true };
    readonly RadioButton _deadLetter = new() { Content = "Dead-letter" };
    readonly ObservableCollection<MessageItem> _messages = new();   // everything held in this tab
    readonly ObservableCollection<MessageItem> _shown = new();      // the filtered view of _messages
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, Margin = new Thickness(16, 0) };
    readonly TextBlock _listInfo = new() { Opacity = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    readonly SelectableTextBlock _details = Ui.Output();
    readonly DockPanel _work = new();
    readonly ProgressBar _progress = Ui.Progress();
    readonly TextBox _body = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 220,
        Watermark = "JSON or text message body", FontFamily = VsCode.Mono,
    };
    readonly TextBox _contentType = new() { Text = "application/json", Watermark = "Content type", MinWidth = 160 };
    readonly TextBox _subject = new() { Watermark = "Subject (optional)", MinWidth = 160 };
    readonly TextBox _properties = new()
    {
        Watermark = "Custom properties as JSON, e.g. {\"source\":\"workbench\",\"retry\":false}",
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55, MaxHeight = 100,
    };
    readonly TextBox _sendAt = new() { Watermark = "Send at (local, empty = now), e.g. 2030-01-31 09:30", MinWidth = 300 };
    readonly Expander _composer;
    readonly Button _refreshCounts;
    readonly Button _peek, _peekNext, _receive, _editAsNew, _resendTo, _moveTo, _complete, _export, _import, _purge, _selectAll;
    readonly Button _copyBody, _copyJson, _findLogs, _cancel;
    readonly TextBox _filter = new() { Watermark = "Filter messages (Ctrl+F)", MinWidth = 260 };
    readonly CheckBox _regex = new() { Content = "Regex" };
    readonly ComboBox _reason = new() { MinWidth = 180, IsVisible = false };
    readonly TextBlock _showing = new() { Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly List<Control> _receiveControls = [];
    bool _listFromDeadLetter;
    bool _lastPeekFull;
    long _nextSequence;
    bool _refreshing;
    bool _closed;
    bool _updatingReasons;
    bool _picking;
    string? _traceId;
    CancellationTokenSource? _cts;

    public QueueRef Queue { get; }
    public bool IsBusy { get; private set; }
    public bool HasReceivedMessages { get; private set; }
    public string? UnsavedWork => HasReceivedMessages ? "received-and-deleted messages" : null;

    /// <summary>The entity sends go to: the queue or topic itself, or the topic of a subscription.</summary>
    QueueRef SendRef => Queue.Kind == EntityKind.Subscription ? Queue with { Subscription = null, IsTopic = true } : Queue;

    public QueueView(QueueRef queue, QueueService service, Action<QueueRef, MessageCountDetails?> countsChanged, IWorkbench? workbench = null)
    {
        _workbench = workbench;
        Queue = queue;
        _service = service;
        _countsChanged = countsChanged;
        // Radio groups must not cross document boundaries.
        _active.GroupName = _deadLetter.GroupName = Guid.NewGuid().ToString();
        _list.ItemsSource = _shown;
        _list.ItemTemplate = new FuncDataTemplate<MessageItem>((m, _) => m == null ? null : Row(
            m.Message.SequenceNumber.ToString(), m.Message.EnqueuedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            m.Message.MessageId, m.Message.Subject, m.Message.DeadLetterReason));
        _list.SelectionChanged += (_, _) => UpdateSelection();

        _peek = Ui.Primary("Peek", () => Run(Peek));
        _peekNext = Ui.Button("Peek next", () => RunLabelled(PeekNext, "Peeking next messages..."));
        _receive = Ui.Button("Receive & delete...", ConfirmReceive);
        _refreshCounts = Ui.Button("Refresh counts", () => _ = RefreshCountsAsync());
        _editAsNew = Ui.Button("Edit as new...", EditAsNew);
        _resendTo = Ui.Button("Resend to...", () => PickTarget(_resendTo!, "Resend selected messages to:", "Resend", move: false));
        _moveTo = Ui.Button("Move to...", () => PickTarget(_moveTo!, "Move selected dead-letter messages to:", "Move", move: true));
        _complete = Ui.Button("Complete selected", ConfirmComplete);
        _export = Ui.Button("Export selected...", () => RunLabelled(Export, "Exporting selected messages..."));
        _import = Ui.Button("Import & send...", () => _ = ImportAsync());
        _purge = Ui.Button("Purge DLQ...", ShowPurge);
        _selectAll = Ui.Button("Select all shown", () => _list.SelectAll());
        _copyBody = Ui.Button("Copy body", () => CopyAsync(_list.SelectedItem is MessageItem m ? m.Message.Body.ToString() : "", "body"));
        _copyJson = Ui.Button("Copy message JSON", () => CopyAsync(_list.SelectedItem is MessageItem m ? MessageArchive.SerializeOne(m.Message) : "", "message JSON"));
        _findLogs = Ui.Button("Find in logs", () => { if (_traceId != null) _workbench?.FindInLogs(_traceId, _findLogs!); });
        _cancel = Ui.Button("Cancel", () => _cts?.Cancel());
        _cancel.IsVisible = false;
        _cancel.Classes.Add("danger");
        _receiveControls.AddRange([_active, _deadLetter, _count, _peek, _peekNext, _receive, _editAsNew, _resendTo, _moveTo,
            _complete, _export, _purge, _selectAll, _filter, _regex, _reason, _showing]);

        _filter.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) ApplyFilter(); };
        _regex.IsCheckedChanged += (_, _) => ApplyFilter();
        _reason.SelectionChanged += (_, _) => { if (!_updatingReasons) ApplyFilter(); };
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(_filter.Text)) return;
            _filter.Text = "";
            e.Handled = true;
        };
        AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            if (e.Key != Key.F || !Ui.IsCommand(e.KeyModifiers) || !Queue.CanReceive) return;
            _filter.Focus();
            _filter.SelectAll();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        var sendNote = Queue.Kind == EntityKind.Subscription
            ? $"Sends from a subscription go to topic {Queue.Name}; every subscription whose rules match receives them."
            : null;
        _composer = new Expander
        {
            Header = "New message", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(16, 0),
            IsExpanded = !Queue.CanReceive,
            Content = new DockPanel
            {
                Children =
                {
                    Ui.Dock(Ui.Primary("Send message", () => Run(Send)), Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer
                    {
                        MaxHeight = 320,
                        Content = new StackPanel
                        {
                            Spacing = 6, Children =
                            {
                                new TextBlock { Text = $"Send to {SendRef.KindLabel} {SendRef.Name}: {queue.Namespace.Host}", TextWrapping = TextWrapping.Wrap },
                                Ui.Hint("Sending creates a real message and may trigger consumers, even when viewing the DLQ."),
                                new TextBlock { Text = "Body" }, _body,
                                new TextBlock { Text = "Content type / subject" }, new WrapPanel { Children = { _contentType, _subject } },
                                new TextBlock { Text = "Custom properties (optional; string, boolean or number values)" }, _properties,
                                new TextBlock { Text = "Scheduled send (optional)" }, _sendAt,
                            },
                        },
                    },
                },
            },
        };

        var header = Ui.Header($"{Capitalize(queue.KindLabel)} · {queue.DisplayName}",
            $"{queue.Namespace.Host} / {queue.ResourcePath}");
        header.Children.Add(_counts);
        var primary = Ui.Toolbar(_active, _deadLetter, _count, _peek, _peekNext, _receive, _refreshCounts);
        var secondary = Ui.Toolbar(_editAsNew, _resendTo, _moveTo, _complete, _export, _import, _purge);
        var filterRow = Ui.Toolbar(_filter, _regex, _reason, _selectAll, _showing);

        var columns = Row("Seq", "Enqueued", "MessageId", "Subject", "DLQ reason");
        columns.Margin = new Thickness(28, 4, 28, 4);
        columns.Opacity = Ui.Muted;
        var detailButtons = Ui.Toolbar(_copyBody, _copyJson, _findLogs);
        detailButtons.Margin = new Thickness(16, 0, 16, 4);
        var panels = new Grid { RowDefinitions = new RowDefinitions("*,5,*") };
        panels.RowDefinitions[0].MinHeight = panels.RowDefinitions[2].MinHeight = 80;
        panels.Children.Add(new DockPanel
        {
            Children =
            {
                Ui.Dock(new Border { Padding = new Thickness(16, 2), Child = _listInfo }, Avalonia.Controls.Dock.Top),
                Ui.Dock(columns, Avalonia.Controls.Dock.Top), _list,
            },
        });
        panels.Children.Add(Ui.At(new GridSplitter(), row: 1));
        panels.Children.Add(Ui.At(new DockPanel
        {
            Children =
            {
                Ui.Dock(Ui.Section("Details"), Avalonia.Controls.Dock.Top),
                Ui.Dock(detailButtons, Avalonia.Controls.Dock.Top),
                new ScrollViewer { Content = _details },
            },
        }, row: 2));
        _receiveControls.AddRange([panels]);

        _work.Children.Add(Ui.Dock(header, Avalonia.Controls.Dock.Top));
        if (sendNote != null)
        {
            var warning = WarningHint(sendNote);
            warning.Margin = new Thickness(16, 0, 16, 4);
            _work.Children.Add(Ui.Dock(warning, Avalonia.Controls.Dock.Top));
        }
        _work.Children.Add(Ui.Dock(primary, Avalonia.Controls.Dock.Top));
        _work.Children.Add(Ui.Dock(secondary, Avalonia.Controls.Dock.Top));
        _work.Children.Add(Ui.Dock(_composer, Avalonia.Controls.Dock.Top));
        _work.Children.Add(Ui.Dock(filterRow, Avalonia.Controls.Dock.Top));
        _work.Children.Add(panels);
        _receiveControls.AddRange([filterRow]);
        if (!queue.CanReceive)
        {
            foreach (var control in _receiveControls)
            {
                control.IsVisible = false;
                control.IsEnabled = false;
            }
            _listInfo.Text = "Topics hold no messages. Open one of its subscriptions to peek or receive.";
        }
        else _listInfo.Text = "Peek or receive to list messages. Ctrl/Shift-click to select.";

        var statusBar = new DockPanel { Children = { Ui.Dock(_cancel, Avalonia.Controls.Dock.Right), _status } };
        _cancel.Margin = new Thickness(0, 0, 16, 0);
        _cancel.VerticalAlignment = VerticalAlignment.Center;
        Content = new DockPanel
        {
            Background = VsCode.Editor,
            Children = { Ui.Dock(_progress, Avalonia.Controls.Dock.Top), Ui.Dock(statusBar, Avalonia.Controls.Dock.Bottom), _work },
        };
        _showing.Text = "";
        UpdateSelection();
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static TextBlock WarningHint(string text)
    {
        var hint = Ui.Hint(text);
        hint.Opacity = 1;
        hint.Foreground = VsCode.Warning;
        return hint;
    }

    int Count => (int)(_count.Value ?? 20);
    bool DeadLetter => _deadLetter.IsChecked == true;
    /// <summary>True when the list shows peeked (still in Azure) dead-letter messages, so move/complete are possible.</summary>
    bool DlqActionsAvailable => Queue.CanReceive && _listFromDeadLetter && !HasReceivedMessages && _messages.Count > 0;

    static string Describe(QueueRef r) => $"{r.KindLabel} · {r.DisplayName} ({r.Namespace.Name})";

    // ---------------- sending ----------------

    async Task<string> Send()
    {
        var message = new MessageDraft(_body.Text ?? "", _contentType.Text ?? "", _subject.Text ?? "", _properties.Text ?? "").ToMessage();
        DateTimeOffset? at = null;
        if (!string.IsNullOrWhiteSpace(_sendAt.Text))
        {
            if (!DateTimeOffset.TryParse(_sendAt.Text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
                throw new ArgumentException($"Could not read '{_sendAt.Text}' as a date and time. Use your local format, e.g. {DateTime.Now.AddHours(1):g}.");
            if (parsed <= DateTimeOffset.Now)
                throw new ArgumentException("The Send at time must be in the future.");
            at = parsed;
        }
        var target = SendRef;
        string result;
        try
        {
            if (at is { } when)
            {
                var sequence = await _service.ScheduleAsync(Queue, message, when);
                result = $"Scheduled one message for {when.ToLocalTime():yyyy-MM-dd HH:mm:ss} to {target.KindLabel} {target.Name} on {Queue.Namespace.Host}. Sequence number: {sequence}. Message ID: {message.MessageId}";
            }
            else
            {
                await _service.SendAsync(Queue, message);
                result = $"Sent one message to {target.KindLabel} {target.Name} on {Queue.Namespace.Host}. Message ID: {message.MessageId}";
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Send was not confirmed; the draft was kept. Delivery may have occurred. Check the {target.KindLabel} before retrying. {ex.Message}", ex);
        }
        _body.Text = "";
        _sendAt.Text = "";
        _composer.IsExpanded = !Queue.CanReceive;
        _ = RefreshCountsAsync();
        return result;
    }

    void EditAsNew()
    {
        if (_list.SelectedItems?.Count != 1 || _list.SelectedItem is not MessageItem item) return;
        var m = item.Message;
        _body.Text = m.Body.ToString();
        _contentType.Text = m.ContentType ?? "";
        _subject.Text = m.Subject ?? "";
        _properties.Text = MessageArchive.PropertiesToJson(m.ApplicationProperties);
        _sendAt.Text = "";
        _composer.IsExpanded = true;
        _status.Text = $"Composer filled from message {m.MessageId}. Sending creates a NEW message; the original is unchanged"
            + (_listFromDeadLetter && !HasReceivedMessages ? " (remove the dead-lettered original afterwards with Complete selected)." : ".");
    }

    // ---------------- peek / receive ----------------

    async Task<string> Peek()
    {
        if (HasReceivedMessages)
            throw new InvalidOperationException("This tab holds received-and-deleted messages. Resend all of them or close this tab before peeking again.");
        var count = Count;
        var dead = DeadLetter;
        var batch = await _service.PeekAsync(Queue, dead, count);
        Show(batch, "Peeked", false, dead);
        _lastPeekFull = batch.Count >= count && batch.Count > 0;
        _nextSequence = batch.Count > 0 ? batch[^1].SequenceNumber + 1 : 0;
        UpdateSelection();
        return "Peek complete.";
    }

    async Task<string> PeekNext()
    {
        if (HasReceivedMessages || !_lastPeekFull)
            throw new InvalidOperationException("Peek next continues a previous peek that returned a full batch.");
        var count = Count;
        var batch = await _service.PeekAsync(Queue, _listFromDeadLetter, count, _nextSequence);
        foreach (var message in batch) _messages.Add(new MessageItem(message));
        _lastPeekFull = batch.Count >= count;
        if (batch.Count > 0) _nextSequence = batch[^1].SequenceNumber + 1;
        _listInfo.Text = ListInfo("Peeked", _messages.Count, _listFromDeadLetter);
        RefreshReasons();
        ApplyFilter();
        return $"Peeked {batch.Count} more. {_messages.Count} held in total.";
    }

    void ConfirmReceive() => Ui.Confirm(_receive,
        $"Messages are removed from Azure and held only in this tab. Closing it or peeking again discards them. Resend them first if needed.\n\nReceive up to {Count} from the {(DeadLetter ? "dead-letter" : "active")} queue of {Describe(Queue)}.",
        "Receive & delete", () => Run(Receive), destructive: true);

    async Task<string> Receive()
    {
        if (HasReceivedMessages)
            throw new InvalidOperationException("Resend the received messages before receiving another batch, or close this tab to discard them.");
        var dead = DeadLetter;
        Show(await _service.ReceiveAndDeleteAsync(Queue, dead, Count), "Received & deleted", true, dead);
        _lastPeekFull = false;
        UpdateSelection();
        _ = RefreshCountsAsync();
        return "Received messages are held only in this tab.";
    }

    static string ListInfo(string action, int count, bool dead) =>
        $"{action}: {count} from {(dead ? "dead-letter" : "active")} queue. Ctrl/Shift-click to select.";

    void Show(List<ServiceBusReceivedMessage> messages, string action, bool received, bool dead)
    {
        _messages.Clear();
        foreach (var message in messages) _messages.Add(new MessageItem(message));
        _listFromDeadLetter = dead;
        HasReceivedMessages = received && messages.Count > 0;
        _listInfo.Text = ListInfo(action, messages.Count, dead);
        RefreshReasons();
        ApplyFilter();
    }

    // ---------------- filtering ----------------

    void RefreshReasons()
    {
        var reasons = _messages.Select(m => m.Message.DeadLetterReason).Where(r => !string.IsNullOrEmpty(r))
            .Distinct().OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        var previous = _reason.SelectedItem as string;
        _updatingReasons = true;
        _reason.ItemsSource = new[] { AnyReason }.Concat(reasons!).ToList();
        _reason.SelectedItem = previous != null && reasons.Contains(previous) ? previous : AnyReason;
        _reason.IsVisible = Queue.CanReceive && reasons.Count > 0;
        _updatingReasons = false;
    }

    /// <summary>Rebuilds the visible list. Hidden messages are deselected so actions can never touch what is not shown.</summary>
    void ApplyFilter()
    {
        LogFilter filter;
        try { filter = new LogFilter(0, _filter.Text, _regex.IsChecked == true); }
        catch (ArgumentException ex)
        {
            _showing.Text = $"Invalid regex: {ex.Message}";
            return;
        }
        var reason = _reason.SelectedIndex > 0 ? _reason.SelectedItem as string : null;
        var keep = _messages.Where(m => MessageFormatter.Matches(m.Message, filter, reason)).ToList();
        var keepSet = keep.ToHashSet();
        var selected = (_list.SelectedItems?.Cast<MessageItem>() ?? []).Where(keepSet.Contains).ToList();
        if (!_shown.SequenceEqual(keep))
        {
            _shown.Clear();
            foreach (var m in keep) _shown.Add(m);
        }
        _list.SelectedItems?.Clear();
        foreach (var m in selected) _list.SelectedItems?.Add(m);
        _showing.Text = $"showing {keep.Count} of {_messages.Count}";
        UpdateSelection();
    }

    // ---------------- selection ----------------

    List<MessageItem> Selected()
    {
        var selected = (_list.SelectedItems?.Cast<MessageItem>() ?? []).ToHashSet();
        var ordered = _shown.Where(selected.Contains).ToList();
        return ordered.Count > 0 ? ordered : throw new InvalidOperationException("Select one or more messages.");
    }

    void UpdateSelection()
    {
        var count = _list.SelectedItems?.Count ?? 0;
        var first = _list.SelectedItem as MessageItem;
        _details.Text = first != null ? MessageFormatter.Describe(first.Message) : "";
        _traceId = first != null ? MessageFormatter.TraceId(first.Message) : null;
        _findLogs.IsVisible = _traceId != null && _workbench != null;
        _copyBody.IsVisible = _copyJson.IsVisible = first != null;
        var can = Queue.CanReceive;
        var dlq = DlqActionsAvailable;
        _editAsNew.IsEnabled = can && count == 1;
        _resendTo.IsEnabled = can && count > 0;
        _export.IsEnabled = can && count > 0;
        _moveTo.IsEnabled = dlq && count > 0;
        _complete.IsEnabled = dlq && count > 0;
        _selectAll.IsEnabled = can && _shown.Count > 0;
        _peekNext.IsEnabled = can && !HasReceivedMessages && _lastPeekFull && _messages.Count > 0;
        _purge.IsEnabled = can;
        _import.IsEnabled = true;
    }

    void RemoveItems(IEnumerable<MessageItem> items)
    {
        foreach (var item in items.ToList())
        {
            _messages.Remove(item);
            _shown.Remove(item);
        }
        if (HasReceivedMessages) HasReceivedMessages = _messages.Count > 0;
        RefreshReasons();
        ApplyFilter();
    }

    // ---------------- resend / move / complete ----------------

    List<Target> Targets()
    {
        var own = SendRef;
        var targets = new List<Target> { new(Queue, $"This entity · {Describe(own)}") };
        var seen = new HashSet<string> { Key(own) };
        foreach (var entity in _workbench?.KnownEntities ?? [])
        {
            var target = entity.Kind == EntityKind.Subscription ? entity with { Subscription = null, IsTopic = true } : entity;
            if (seen.Add(Key(target))) targets.Add(new Target(target, Describe(target)));
        }
        return targets;
        static string Key(QueueRef r) => $"{r.Namespace.Host}|{r.IsTopic}|{r.Name}".ToLowerInvariant();
    }

    void PickTarget(Control anchor, string prompt, string action, bool move)
    {
        List<MessageItem> selected;
        try { selected = Selected(); }
        catch (InvalidOperationException ex) { _status.Text = ex.Message; return; }
        Ui.Pick(anchor, prompt, Targets(), t => t.Label, target =>
        {
            var note = target.Ref == Queue && Queue.Kind == EntityKind.Subscription
                ? $"\n\nThis sends to topic {Queue.Name}; every subscription whose rules match receives the messages." : "";
            var cross = !string.Equals(target.Ref.Namespace.Host, Queue.Namespace.Host, StringComparison.OrdinalIgnoreCase)
                ? $"\nThe target is on another namespace ({target.Ref.Namespace.Host})." : "";
            var how = move
                ? "Each message is sent to the target first, then removed from the dead-letter queue, so a failure can leave a duplicate but never loses a message."
                : "Source messages in Azure are unchanged.";
            Ui.Confirm(anchor,
                $"{action} {selected.Count} message{(selected.Count == 1 ? "" : "s")}: {Describe(Queue)}{(move ? " (dead-letter)" : "")} → {target.Label}.{cross}\n{how}{note}",
                action, () => RunLabelled(() => move ? Move(target.Ref) : Resend(target.Ref), move ? "Moving selected DLQ messages..." : "Resending selected messages..."));
        });
    }

    async Task<string> Resend(QueueRef? target = null)
    {
        target ??= Queue;
        var selected = Selected();
        await _service.ResendAsync(target, selected.Select(m => m.Message));
        if (HasReceivedMessages) RemoveItems(selected);
        _ = RefreshCountsAsync();
        return $"Resent {selected.Count} to {Describe(target)}. Source messages in Azure are unchanged.";
    }

    async Task<string> Move(QueueRef? target = null)
    {
        target ??= Queue;
        if (!DlqActionsAvailable)
            throw new InvalidOperationException("Peek the dead-letter queue and select messages to move. For received-and-deleted messages, use Resend to.");
        var selected = Selected();
        var moved = await _service.MoveFromDeadLetterAsync(Queue, selected.Select(m => m.Message.SequenceNumber).ToHashSet(), target);
        RemoveItems(selected.Where(m => moved.Contains(m.Message.SequenceNumber)));
        _ = RefreshCountsAsync();
        return $"Moved {moved.Count} to {Describe(target)}. {selected.Count - moved.Count} not found.";
    }

    void ConfirmComplete()
    {
        List<MessageItem> selected;
        try { selected = Selected(); }
        catch (InvalidOperationException ex) { _status.Text = ex.Message; return; }
        Ui.Confirm(_complete,
            $"Permanently remove {selected.Count} selected dead-lettered message{(selected.Count == 1 ? "" : "s")} from {Describe(Queue)} without resending them? This cannot be undone.",
            "Remove messages", () => RunLabelled(Complete, "Removing selected DLQ messages..."), destructive: true);
    }

    async Task<string> Complete()
    {
        if (!DlqActionsAvailable)
            throw new InvalidOperationException("Peek the dead-letter queue and select messages to remove.");
        var selected = Selected();
        var done = await _service.CompleteFromDeadLetterAsync(Queue, selected.Select(m => m.Message.SequenceNumber).ToHashSet());
        RemoveItems(selected.Where(m => done.Contains(m.Message.SequenceNumber)));
        _ = RefreshCountsAsync();
        return $"Removed {done.Count} from the dead-letter queue. {selected.Count - done.Count} not found.";
    }

    // ---------------- purge ----------------

    void ShowPurge() => PurgeFlyout().ShowAt(_purge);

    /// <summary>Type-the-name confirmation for purging the dead-letter queue (separate so checks can exercise it).</summary>
    Flyout PurgeFlyout()
    {
        var flyout = new Flyout();
        var typed = new TextBox { Watermark = $"Type {Queue.DisplayName} to confirm", MinWidth = 280 };
        var purge = Ui.Button("Purge dead-letter queue", () =>
        {
            flyout.Hide();
            RunLabelled(Purge, "Purging dead-letter queue...", cancellable: true);
        });
        purge.Classes.Add("danger");
        purge.IsEnabled = false;
        typed.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) purge.IsEnabled = typed.Text == Queue.DisplayName; };
        flyout.Content = new StackPanel
        {
            Spacing = 10, MaxWidth = 460,
            Children =
            {
                new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"Permanently delete every message in the dead-letter queue of {Describe(Queue)}. This cannot be undone and only the dead-letter queue is affected; the active queue is left alone.",
                },
                typed,
                Ui.Row(purge, Ui.Button("Cancel", flyout.Hide)),
            },
        };
        return flyout;
    }

    async Task<string> Purge()
    {
        using var cts = _cts = new CancellationTokenSource();
        var purged = 0;
        try
        {
            purged = await _service.PurgeDeadLetterAsync(Queue, n =>
            {
                purged = n;
                Dispatcher.UIThread.Post(() => { if (IsBusy) _status.Text = $"Purged {n}…"; });
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Afterwards();
            return $"Purge cancelled after {purged} message{(purged == 1 ? "" : "s")}.";
        }
        finally { _cts = null; }
        Afterwards();
        return $"Purged {purged} message{(purged == 1 ? "" : "s")} from the dead-letter queue of {Describe(Queue)}.";

        void Afterwards()
        {
            // peeked dead-letter rows are gone from Azure now; received-and-deleted rows are held here and stay
            if (_listFromDeadLetter && !HasReceivedMessages) RemoveItems(_messages);
            _ = RefreshCountsAsync();
        }
    }

    // ---------------- export / import ----------------

    TopLevel Window => TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("The tab is not attached to a window.");

    async Task<string> Export()
    {
        var selected = Selected();
        var json = MessageArchive.Serialize(selected.Select(m => m.Message));
        var file = await Window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export messages", SuggestedFileName = $"{Queue.DisplayName.Replace('/', '-')}-messages.json",
            DefaultExtension = "json", FileTypeChoices = [JsonFiles],
        });
        if (file == null) return "Export cancelled.";
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false))) await writer.WriteAsync(json);
        return $"Exported {selected.Count} message{(selected.Count == 1 ? "" : "s")} to {file.Name}.";
    }

    async Task ImportAsync()
    {
        if (IsBusy || _picking || _closed) return;
        _picking = true;
        try
        {
            var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import messages", AllowMultiple = false, FileTypeFilter = [JsonFiles],
            });
            if (files.Count == 0) return;
            string text;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream)) text = await reader.ReadToEndAsync();
            var messages = MessageArchive.Parse(text);
            if (messages.Count == 0) { _status.Text = "The file contains no messages."; return; }
            var target = SendRef;
            Ui.Confirm(_import,
                $"Send {messages.Count} message{(messages.Count == 1 ? "" : "s")} from {files[0].Name} to {Describe(target)}?\n\nMessage IDs from the file are kept so duplicate detection can reject repeats; sending creates new messages and may trigger consumers."
                + (Queue.Kind == EntityKind.Subscription ? $"\n\nThis sends to topic {Queue.Name}; every subscription whose rules match receives them." : ""),
                "Send", () => RunLabelled(() => SendImported(messages), "Sending imported messages..."));
        }
        catch (Exception ex) { _status.Text = $"Import failed: {ex.Message}"; }
        finally { _picking = false; }
    }

    async Task<string> SendImported(List<ArchivedMessage> messages)
    {
        try { await _service.SendManyAsync(Queue, messages.Select(MessageArchive.ToMessage)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Send was not confirmed; some messages may have been delivered. Check the {SendRef.KindLabel} before retrying. {ex.Message}", ex);
        }
        _ = RefreshCountsAsync();
        return $"Sent {messages.Count} imported message{(messages.Count == 1 ? "" : "s")} to {Describe(SendRef)}.";
    }

    async void CopyAsync(string text, string what)
    {
        try { _status.Text = await Ui.CopyAsync(this, text) ? $"Copied {what} to the clipboard." : "No clipboard is available."; }
        catch (Exception ex) { _status.Text = $"Copy failed: {ex.Message}"; }
    }

    // ---------------- counts ----------------

    public void ShowCounts(MessageCountDetails? counts)
    {
        if (_closed) return;
        _counts.Text = $"{counts?.ActiveMessageCount} active · {counts?.DeadLetterMessageCount} dead-lettered · {counts?.ScheduledMessageCount} scheduled · updated {DateTime.Now:HH:mm:ss}";
    }

    public async Task RefreshCountsAsync()
    {
        if (_closed || _refreshing) return;
        _refreshing = true;
        _refreshCounts.IsEnabled = false;
        _refreshCounts.Content = "Refreshing counts...";
        try
        {
            var counts = await _service.GetCountsAsync(Queue);
            if (!_closed)
            {
                ShowCounts(counts);
                _countsChanged(Queue, counts);
            }
        }
        catch (Exception ex) { if (!_closed) _status.Text = $"Count refresh failed: {ex.Message}"; }
        finally
        {
            _refreshing = false;
            _refreshCounts.IsEnabled = !_closed;
            _refreshCounts.Content = "Refresh counts";
        }
    }

    // ---------------- running operations ----------------

    void Run(Func<Task<string>> operation) => RunLabelled(operation, operation.Method.Name switch
    {
        nameof(Send) => "Sending message...",
        nameof(Move) => "Moving selected DLQ messages...",
        nameof(Resend) => "Resending selected messages...",
        nameof(Receive) => "Receiving and deleting messages...",
        nameof(Peek) => "Peeking messages...",
        _ => "Working...",
    });

    async void RunLabelled(Func<Task<string>> operation, string label, bool cancellable = false)
    {
        if (IsBusy || _closed) return;
        IsBusy = true;
        _work.IsEnabled = false;
        _progress.IsVisible = true;
        _status.Text = label;
        try
        {
            var result = operation();
            _cancel.IsVisible = cancellable && _cts != null;
            _status.Text = await result;
        }
        catch (Exception ex) { _status.Text = $"Queue operation failed: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            _progress.IsVisible = false;
            _cancel.IsVisible = false;
            _work.IsEnabled = true;
            UpdateSelection();
        }
    }

    public Task CloseAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for this queue's operation to finish before closing it.");
        _closed = true;
        return Task.CompletedTask;
    }

    static Grid Row(params string?[] values)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        for (var i = 0; i < values.Length; i++)
            grid.Children.Add(Ui.At(new TextBlock { Text = values[i], TextTrimming = TextTrimming.CharacterEllipsis }, col: i));
        return grid;
    }
}
