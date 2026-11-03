using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

namespace AzureWorkbench.Views;

public sealed class AppLogsView : UserControl
{
    readonly AppLogService _service;
    readonly ContentControl _header = new() { Content = Ui.Header("Logs") };
    readonly TextBlock _status = Ui.StatusLine();
    readonly ComboBox _insights = new() { MinWidth = 200, MaxWidth = 400, PlaceholderText = "Select Application Insights" };
    readonly NumericUpDown _minutes = new() { Minimum = 1, Maximum = 1440, Value = 30, Width = 110, FormatString = "0" };
    readonly ComboBox _severity = new()
    {
        ItemsSource = new[] { "All levels", "Information+", "Warning+", "Error+", "Critical" }, SelectedIndex = 0, Width = 150,
    };
    readonly TextBox _search = new() { Watermark = "Message contains...", Width = 220 };
    readonly CheckBox _traces = new() { Content = "Traces", IsChecked = true };
    readonly CheckBox _exceptions = new() { Content = "Exceptions", IsChecked = true };
    readonly CheckBox _requests = new() { Content = "Requests", IsChecked = false };
    readonly CheckBox _dependencies = new() { Content = "Dependencies", IsChecked = false };
    readonly TextBox _operation = new() { Watermark = "Operation ID (optional)", Width = 260 };
    readonly TextBox _role = new() { Watermark = "Role name (optional)", Width = 200 };
    readonly Button _load;
    readonly Button _reload;
    readonly Button _start;
    readonly Button _stop;
    readonly Button _cancelQuery;
    readonly ProgressBar _progress = Ui.Progress();
    readonly SelectableTextBlock _recent = Ui.Output();
    readonly SelectableTextBlock _live = Ui.Output();
    readonly ScrollViewer _liveScroll;
    readonly LogBuffer _buffer = new();
    readonly object _bufferLock = new();
    readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly CancellationTokenSource _lifetime = new();
    readonly CheckBox _follow = new() { Content = "Follow output", IsChecked = true };
    readonly TextBox _filterText = new() { Watermark = "Filter output (Ctrl+F)", Width = 260 };
    readonly ComboBox _filterLevel = new() { ItemsSource = LogFilter.LevelNames, SelectedIndex = 0, Width = 150 };
    readonly CheckBox _filterRegex = new() { Content = "Regex" };
    readonly TextBlock _filterInfo = new() { Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly TabControl _tabs;
    List<LogEntry> _entries = [];
    LogFilter _filter = new();
    string _liveInfo = "";
    string _recentInfo = "";
    CancellationTokenSource? _selection;
    CancellationTokenSource? _query;
    CancellationTokenSource? _stream;
    Task _tailTask = Task.CompletedTask;
    WebAppRef? _app;
    bool _dirty;
    bool _closed;
    bool _loadingInsights;

    readonly IWorkbench? _workbench;

    /// <summary>The app this tab shows, once selected.</summary>
    public WebAppRef? App => _app;

    public AppLogsView(AppLogService service, IWorkbench? workbench = null)
    {
        _workbench = workbench;
        _service = service;
        _load = Ui.Primary("Refresh recent", async () => await LoadAsync());
        _reload = Ui.Button("Reload Insights list", async () =>
        {
            if (_app != null && _selection != null) await LoadInsightsAsync(_app, _selection.Token);
        });
        _start = Ui.Button("Start live tail", async () => await StartAsync());
        _stop = Ui.Button("Stop", Stop);
        _cancelQuery = Ui.Button("Cancel query", () => _query?.Cancel());
        _stop.IsEnabled = false;
        _liveScroll = new ScrollViewer { Content = _live };
        AddCopyMenu(_recent);
        AddCopyMenu(_live);
        _filterText.TextChanged += (_, _) => UpdateFilter();
        _filterLevel.SelectionChanged += (_, _) => UpdateFilter();
        _filterRegex.IsCheckedChanged += (_, _) => UpdateFilter();
        _filterText.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _filterText.Text = "";
            e.Handled = true;
        };
        var filterBar = Ui.Toolbar(new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center },
            _filterLevel, _filterText, _filterRegex,
            Ui.Button("Clear filter", () =>
            {
                _filterText.Text = "";
                _filterLevel.SelectedIndex = 0;
                _filterRegex.IsChecked = false;
            }),
            Ui.Button("Copy visible", async () => await CopyVisibleAsync()),
            _filterInfo);
        var recentTools = new StackPanel
        {
            Children =
            {
                Ui.Toolbar(_insights, _reload, new TextBlock { Text = "Minutes", VerticalAlignment = VerticalAlignment.Center },
                    _minutes, _severity, _role),
                Ui.Toolbar(_traces, _exceptions, _requests, _dependencies, _search, _operation,
                    _load, _cancelQuery),
            },
        };
        var recentPanel = new DockPanel
        {
            Children =
            {
                Ui.Dock(recentTools, Avalonia.Controls.Dock.Top),
                Ui.Dock(Ui.Hint("Newest 500 of the selected telemetry types. Ingestion can lag. A shared Insights resource includes other apps unless you filter by cloud role name. " +
                    "To follow one operation, paste its Operation ID and click Refresh recent; the Minutes value still limits how far back to look (up to 1440 = 24 h)."), Avalonia.Controls.Dock.Top),
                new ScrollViewer { Content = _recent },
            },
        };
        var livePanel = new DockPanel
        {
            Children =
            {
                Ui.Dock(Ui.Toolbar(_start, _stop, _follow, Ui.Button("Clear", ClearBuffer)), Avalonia.Controls.Dock.Top),
                Ui.Dock(Ui.Hint("Kudu stream: logging must already be enabled. Only one instance may be shown. Linux Consumption/Flex may not support it. No Azure settings are changed. " +
                    "Levels are detected from each line's text; unlabelled lines (e.g. stack traces) follow the line above. Selecting text pauses the display so you can copy it."), Avalonia.Controls.Dock.Top),
                _liveScroll,
            },
        };
        _tabs = Ui.Tabs(("Recent / Application Insights", recentPanel), ("Live tail / Kudu", livePanel));
        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _tabs)) UpdateFilterInfo();
        };
        Content = new DockPanel
        {
            Background = VsCode.Editor,
            Children =
            {
                Ui.Dock(_header, Avalonia.Controls.Dock.Top),
                Ui.Dock(_progress, Avalonia.Controls.Dock.Top),
                Ui.Dock(filterBar, Avalonia.Controls.Dock.Top),
                Ui.Dock(_status, Avalonia.Controls.Dock.Bottom),
                _tabs,
            },
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        UpdateActivity();
    }

    public async Task SelectAsync(WebAppRef app)
    {
        _selection?.Cancel();
        _selection?.Dispose();
        _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _query?.Cancel();
        Stop();
        _app = app;
        _header.Content = Ui.Header($"{(app.IsFunction ? "Function App" : "App Service")} / {app.Name} · logs", app.Id);
        _entries = [];
        RenderRecent();
        ClearBuffer();
        _role.Text = "";
        _insights.ItemsSource = null;
        _insights.SelectedItem = null;
        await LoadInsightsAsync(app, _selection.Token);
    }

    async Task LoadInsightsAsync(WebAppRef app, CancellationToken ct)
    {
        if (_closed || _loadingInsights || _query != null) return;
        _loadingInsights = true;
        _status.Text = "Loading Application Insights resources...";
        UpdateActivity();
        try
        {
            var resources = await _service.GetInsightsAsync(app, ct);
            ct.ThrowIfCancellationRequested();
            if (_app != app) return;
            _insights.ItemsSource = resources;
            _insights.SelectedItem = resources.FirstOrDefault(r =>
                string.Equals(r.Id, app.InsightsResourceId, StringComparison.OrdinalIgnoreCase));
            _status.Text = resources.Count == 0
                ? "No Application Insights resources found in this subscription. Recent logs need Insights; live tail can still work."
                : _insights.SelectedItem == null
                    ? "Select this app's Application Insights resource. No link was discovered; resources are not assigned automatically."
                    : "Linked Application Insights selected. Click Refresh recent. For shared resources, set a cloud role name filter.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (_app == app && !ct.IsCancellationRequested) ShowError(ex); }
        finally
        {
            _loadingInsights = false;
            UpdateActivity();
        }
    }

    /// <summary>Shows every telemetry item of one operation (last 24 h) on the Recent tab.</summary>
    public async Task FindOperationAsync(string operationId)
    {
        if (_closed) return;
        _tabs.SelectedIndex = 0;
        _operation.Text = operationId;
        _traces.IsChecked = _exceptions.IsChecked = _requests.IsChecked = _dependencies.IsChecked = true;
        _minutes.Value = 1440;
        // A running query is cancelled first; its finally block clears _query.
        _query?.Cancel();
        while ((_query != null || _loadingInsights) && !_closed) await Task.Delay(20);
        if (_closed || _app == null) return;
        if (_insights.SelectedItem is not InsightsRef)
        {
            _status.Text = "Select this app's Application Insights resource, then click Refresh recent.";
            return;
        }
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_closed || _loadingInsights || _query != null) return;
        if (_app is not { } app) return;
        if (_insights.SelectedItem is not InsightsRef insights)
        {
            _status.Text = "Select an Application Insights resource first. Apps without Insights have no queryable history here.";
            return;
        }
        var types = (_traces.IsChecked == true ? TelemetryTypes.Traces : 0)
            | (_exceptions.IsChecked == true ? TelemetryTypes.Exceptions : 0)
            | (_requests.IsChecked == true ? TelemetryTypes.Requests : 0)
            | (_dependencies.IsChecked == true ? TelemetryTypes.Dependencies : 0);
        if (types == 0)
        {
            _status.Text = "Select at least one telemetry type: Traces, Exceptions, Requests or Dependencies.";
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _selection!.Token);
        _query = cts;
        UpdateActivity();
        _status.Text = "Querying recent logs...";
        try
        {
            var logs = await _service.QueryAsync(app, insights, (int)(_minutes.Value ?? 30),
                _severity.SelectedIndex, _search.Text ?? "", _role.Text ?? "", cts.Token, types, _operation.Text ?? "");
            cts.Token.ThrowIfCancellationRequested();
            _entries = logs;
            RenderRecent();
            _status.Text = $"{logs.Count} entries returned at {DateTime.Now:HH:mm:ss}. " +
                (logs.Count == 0 ? "Check the selected resource, time range, role filter and whether telemetry is enabled." : "Refresh manually for new entries.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_app == app && !_closed) _status.Text = "Query cancelled.";
        }
        catch (Exception ex) { if (_app == app && !cts.IsCancellationRequested) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_query, cts))
            {
                _query = null;
                UpdateActivity();
            }
            cts.Dispose();
        }
    }

    async Task StartAsync()
    {
        if (_app is not { } app || _closed) return;
        _start.IsEnabled = false;
        // A stopped stream must finish before the next one starts.
        await _tailTask;
        if (_app != app || _closed) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _selection!.Token);
        _stream = cts;
        UpdateActivity();
        _stop.IsEnabled = true;
        _tailTask = TailAsync(app, cts);
        await _tailTask;
    }

    async Task TailAsync(WebAppRef app, CancellationTokenSource cts)
    {
        _status.Text = "Live tail requested. Waiting for output; the app may be idle or application logging may be disabled.";
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var chunk in _service.TailAsync(app, cts.Token))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    lock (_bufferLock)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        _buffer.Append(chunk);
                        _dirty = true;
                    }
                }
            }, cts.Token);
            if (_app == app && !_closed) _status.Text = "Live stream ended. Click Start to reconnect.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (_app == app && !_closed) _status.Text = "Live tail stopped.";
        }
        catch (Exception ex) { if (_app == app && !cts.IsCancellationRequested) ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_stream, cts))
            {
                _stream = null;
                UpdateActivity();
                _start.IsEnabled = !_closed;
                _stop.IsEnabled = false;
                Flush();
            }
            cts.Dispose();
        }
    }

    public void Pause()
    {
        _selection?.Cancel();
        _query?.Cancel();
        Stop();
    }

    public async Task CloseAsync()
    {
        _closed = true;
        _flush.Stop();
        _lifetime.Cancel();
        Pause();
        await _tailTask;
        _selection?.Dispose();
        _lifetime.Dispose();
    }

    void Stop()
    {
        _stream?.Cancel();
        _stop.IsEnabled = false;
    }

    void Flush(bool force = false)
    {
        // Replacing the text clears any selection, so hold updates while the user is selecting/copying.
        if (!force && _live.SelectionStart != _live.SelectionEnd)
        {
            if (_dirty) _liveInfo = "Display paused while text is selected. Click the output to resume.";
            UpdateFilterInfo();
            return;
        }
        string raw;
        bool truncated;
        lock (_bufferLock)
        {
            if ((!_dirty && !force) || _closed) return;
            _dirty = false;
            raw = _buffer.ToString();
            truncated = _buffer.Truncated;
        }
        var text = _filter.Apply(raw, out var shown, out var total);
        _liveInfo = _filter.IsEmpty ? "" : $"Live: showing {shown:N0} of {total:N0} lines";
        _live.Text = (truncated ? "[Older output trimmed; showing the last 200,000 characters]\n" : "") + text;
        UpdateFilterInfo();
        if (_follow.IsChecked == true) _liveScroll.ScrollToEnd();
    }

    void RenderRecent()
    {
        var text = new StringBuilder();
        const int limit = 200_000;
        var trimmed = false;
        var shown = 0;
        foreach (var log in _entries.Where(_filter.Matches))
        {
            shown++;
            if (trimmed) continue;
            var line = $"{log.Timestamp} [{Level(log.Severity)}] {log.ItemType} {log.Role} op:{log.OperationId}\n{log.Message}\n\n";
            var remaining = limit - text.Length;
            text.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
            trimmed = line.Length > remaining;
        }
        _recent.Text = text + (trimmed ? "\n[Remaining output trimmed at 200,000 characters]" : "");
        _recentInfo = _filter.IsEmpty || _entries.Count == 0 ? "" : $"Recent: showing {shown:N0} of {_entries.Count:N0} entries";
        UpdateFilterInfo();
    }

    void UpdateFilter()
    {
        try { _filter = new LogFilter(_filterLevel.SelectedIndex, _filterText.Text, _filterRegex.IsChecked == true); }
        catch (ArgumentException ex)
        {
            _filterInfo.Text = $"Invalid regex: {ex.Message}";
            return;
        }
        RenderRecent();
        Flush(force: true);
    }

    void UpdateFilterInfo() =>
        _filterInfo.Text = _tabs.SelectedIndex == 1 ? _liveInfo : _recentInfo;

    SelectableTextBlock VisibleOutput => _tabs.SelectedIndex == 1 ? _live : _recent;

    async Task CopyVisibleAsync()
    {
        var text = VisibleOutput.Text ?? "";
        _status.Text = await Ui.CopyAsync(this, text)
            ? $"Copied {text.Length:N0} characters of {(_tabs.SelectedIndex == 1 ? "live" : "recent")} output."
            : "Clipboard is not available.";
    }

    void AddCopyMenu(SelectableTextBlock output)
    {
        var copy = new MenuItem { Header = "Copy selection" };
        copy.Click += (_, _) => output.Copy();
        var all = new MenuItem { Header = "Select all" };
        all.Click += (_, _) => output.SelectAll();
        var visible = new MenuItem { Header = "Copy all visible output" };
        visible.Click += async (_, _) => await CopyVisibleAsync();
        output.ContextMenu = new ContextMenu { Items = { copy, all, visible } };
        output.PointerReleased += (_, _) => { if (output.SelectionStart == output.SelectionEnd) Flush(); };
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Ui.IsCommand(e.KeyModifiers))
        {
            _filterText.Focus();
            _filterText.SelectAll();
            e.Handled = true;
        }
    }

    void ClearBuffer()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
            _dirty = true;
        }
    }

    void ShowError(Exception ex) => _status.Text = $"Log error: {ex.Message}";

    void UpdateActivity()
    {
        var querying = _loadingInsights || _query != null;
        _progress.IsVisible = !_closed && (querying || _stream != null);
        _reload.IsEnabled = _load.IsEnabled = _insights.IsEnabled = !_closed && !querying;
        _minutes.IsEnabled = _severity.IsEnabled = _search.IsEnabled = _role.IsEnabled = !_closed && !querying;
        _traces.IsEnabled = _exceptions.IsEnabled = _requests.IsEnabled = _dependencies.IsEnabled = _operation.IsEnabled = !_closed && !querying;
        _cancelQuery.IsEnabled = !_closed && _query != null;
    }

    static string Level(string level) => level switch
    {
        "0" => "Verbose", "1" => "Information", "2" => "Warning", "3" => "Error", "4" => "Critical", _ => level,
    };
}
