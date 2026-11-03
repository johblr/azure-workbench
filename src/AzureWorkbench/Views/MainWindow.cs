using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using G = AzureWorkbench.Views.VsCode.Glyphs;

namespace AzureWorkbench.Views;

public class MainWindow : Window, IWorkbench
{
    readonly SettingsStore _store = new();
    readonly WorkbenchSettings _settings;
    readonly CredentialProvider _credentials;
    readonly ResourceExplorer _explorer;
    readonly QueueService _queues = new();
    readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    readonly AppLogService _appLogs;
    readonly AppConfigService _appConfig;
    readonly PinResolver _pins;
    readonly ResourceIndex _index;
    readonly ListBox _results = new() { IsVisible = false, MaxHeight = 320, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _resultsHeader = new() { IsVisible = false };
    readonly TextBlock _indexInfo = new() { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    List<QuickOpenItem> _resultItems = [];
    readonly DeploymentService _deployments;
    readonly WorkspaceTabs _workspace;
    readonly QuickOpen _quickOpen;
    readonly TextBlock _status = new()
    {
        Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        Text = "Ready. Azure CLI sign-in uses your existing az login.",
    };
    readonly TextBlock _account = new() { Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Text = "Not signed in" };
    readonly TreeView _tree = new();
    readonly TextBox _filter = new() { Watermark = "Search all resources", Margin = new Thickness(10, 0, 10, 6), FontSize = 12 };
    readonly ProgressBar _progress = Ui.Progress();
    readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("300,4,*") };
    readonly Control _sidebar;
    readonly StackPanel _signIn;
    readonly DispatcherTimer _autoRefresh = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly Dictionary<TreeViewItem, string> _tenantOf = new();
    TreeViewItem? _pinned;
    CancellationTokenSource _session = new();
    bool _signedIn;
    bool _busy;
    bool _refreshing;
    bool _allowClose;
    bool _closing;

    public MainWindow()
    {
        _settings = _store.Load();
        _credentials = new CredentialProvider(_store.Directory, () => _settings.AllowUnencryptedTokenCache);
        _credentials.Notice += message => Dispatcher.UIThread.Post(() => _status.Text = message);
        _explorer = new ResourceExplorer(_credentials);
        _appLogs = new AppLogService(_http);
        _appConfig = new AppConfigService(_http);
        _pins = new PinResolver(_credentials, _http);
        _deployments = new DeploymentService(_http);
        _index = new ResourceIndex(_explorer, _appLogs, _credentials);
        _index.Changed += OnIndexChanged;
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedIndex < 0 || _results.SelectedIndex >= _resultItems.Count) return;
            var item = _resultItems[_results.SelectedIndex];
            _results.SelectedIndex = -1;
            item.Run();
        };
        _workspace = new WorkspaceTabs(Notify);
        _quickOpen = new QuickOpen(QuickOpenItems);
        _signIn = Ui.Row(SignInButton("Sign in with Azure CLI", AuthMode.AzureCli),
            SignInButton("Sign in with browser", AuthMode.InteractiveBrowser));
        Title = "Azure Workbench";
        using (var icon = AssetLoader.Open(new Uri("avares://AzureWorkbench/Assets/App.ico")))
            Icon = new WindowIcon(icon);
        Width = 1440;
        Height = 880;
        MinWidth = 760;
        MinHeight = 420;
        FontSize = 13;
        Background = VsCode.Editor;
        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(12, 3)) },
        });
        Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(MinHeightProperty, 24.0), new Setter(FontSizeProperty, 13.0) },
        });
        _sidebar = BuildSidebar();
        Content = BuildLayout();
        _tree.SelectionChanged += (_, _) => OpenSelected();
        _tree.DoubleTapped += (_, _) => OpenSelected();
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _filter.Text = "";
            e.Handled = true;
        };
        _autoRefresh.Tick += async (_, _) => await AutoRefreshAsync();
        _autoRefresh.Start();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    /// <summary>Signs in automatically with the last method when that needs no prompt (Azure CLI, or a remembered browser account).</summary>
    public void StartAutoSignIn()
    {
        if (_settings.LastAuthMode == AuthMode.AzureCli) Run(() => SignInAsync(AuthMode.AzureCli));
        else if (_settings.LastAuthMode == AuthMode.InteractiveBrowser
            && Directory.Exists(_store.Directory) && Directory.GetFiles(_store.Directory, "auth-*.json").Length > 0)
            Run(() => SignInAsync(AuthMode.InteractiveBrowser));
    }

    // ---------------------------------------------------------------- layout

    Control BuildLayout()
    {
        var search = new Button
        {
            Content = new TextBlock { Text = $"Search resources and commands ({Shortcut("P")})", Opacity = 0.8 },
            HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 360, Background = VsCode.SideBar,
            Padding = new Thickness(12, 2), FontSize = 12,
        };
        search.Click += (_, _) => _quickOpen.Show(commands: false);
        var title = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                Ui.At(new TextBlock { Text = "Azure Workbench", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 16, 0) }, col: 0),
                Ui.At(search, col: 1),
                Ui.At(_signIn, col: 2),
            },
        };
        Button Tool(string text, string tip, Action action)
        {
            var b = Ui.Button(text, action);
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            return b;
        }
        var tools = Ui.Row(
            Tool("Split right", $"Split the active tab into a new group on the right ({Shortcut("\\")})", () => _workspace.MoveActive(1, true)),
            Tool("Split down", $"Split the active tab into a new group below ({Shortcut("Shift+\\")})", _workspace.SplitActiveDown),
            Tool("Move left", "Move the active tab to the group on the left", () => _workspace.MoveActive(-1)),
            Tool("Move right", "Move the active tab to the group on the right", () => _workspace.MoveActive(1)));
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        var editors = new DockPanel
        {
            Children =
            {
                Ui.Dock(new Border { Background = VsCode.SideBar, Padding = new Thickness(8, 1), Child = tools }, Avalonia.Controls.Dock.Top),
                _workspace,
            },
        };
        _body.ColumnDefinitions[0].MinWidth = 200;
        _body.ColumnDefinitions[2].MinWidth = 400;
        _body.Children.Add(_sidebar);
        _body.Children.Add(Ui.At(new GridSplitter { Background = VsCode.Border }, col: 1));
        _body.Children.Add(Ui.At(editors, col: 2));
        var status = new DockPanel
        {
            Children =
            {
                Ui.Dock(new TextBlock { Text = $"{Shortcut("P")} quick open  ·  {Shortcut("B")} sidebar", Foreground = Brushes.White, FontSize = 12,
                    Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }, Avalonia.Controls.Dock.Right),
                Ui.Dock(_account, Avalonia.Controls.Dock.Right),
                Ui.Dock(_indexInfo, Avalonia.Controls.Dock.Right),
                _status,
            },
        };
        var shell = new DockPanel
        {
            Children =
            {
                Ui.Dock(new Border { Background = VsCode.TitleBar, Padding = new Thickness(12, 4), Child = title }, Avalonia.Controls.Dock.Top),
                Ui.Dock(_progress, Avalonia.Controls.Dock.Top),
                Ui.Dock(new Border { Background = VsCode.StatusBar, Padding = new Thickness(10, 2), Child = status }, Avalonia.Controls.Dock.Bottom),
                _body,
            },
        };
        return new Panel { Children = { shell, _quickOpen } };
    }

    Control BuildSidebar()
    {
        var collapse = Ui.Button("Collapse", () =>
        {
            foreach (var node in Nodes(_tree.Items)) node.IsExpanded = false;
        });
        collapse.Padding = new Thickness(6, 0);
        collapse.FontSize = 11;
        collapse.Background = Brushes.Transparent;
        ToolTip.SetTip(collapse, "Collapse all");
        return new DockPanel
        {
            Background = VsCode.SideBar,
            Children =
            {
                Ui.Dock(new DockPanel
                {
                    Margin = new Thickness(0, 6, 6, 2),
                    Children = { Ui.Dock(collapse, Avalonia.Controls.Dock.Right), Ui.Section("Explorer") },
                }, Avalonia.Controls.Dock.Top),
                Ui.Dock(_filter, Avalonia.Controls.Dock.Top),
                Ui.Dock(_resultsHeader, Avalonia.Controls.Dock.Top),
                Ui.Dock(_results, Avalonia.Controls.Dock.Top),
                _tree,
            },
        };
    }

    static string Shortcut(string key) => OperatingSystem.IsMacOS() ? $"⌘{key}" : $"Ctrl+{key}";

    void ToggleSidebar()
    {
        var visible = !_sidebar.IsVisible;
        _sidebar.IsVisible = visible;
        _body.ColumnDefinitions[0].MinWidth = visible ? 200 : 0;
        _body.ColumnDefinitions[0].Width = visible ? new GridLength(300) : new GridLength(0);
        _body.ColumnDefinitions[1].Width = new GridLength(visible ? 4 : 0);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _quickOpen.IsVisible)
        {
            _quickOpen.Hide();
            e.Handled = true;
            return;
        }
        if (!Ui.IsCommand(e.KeyModifiers)) return;
        if (e.Key == Key.P)
        {
            _quickOpen.Show(commands: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.B && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ToggleSidebar();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- sign-in

    Button SignInButton(string label, AuthMode mode)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) =>
        {
            if (_workspace.Documents.Any())
                Ui.Confirm(button,
                    "Signing in again closes all tabs and stops their log streams. Unsaved configuration edits and received-and-deleted messages held only in tabs will be discarded.",
                    "Close tabs and sign in", () => Run(() => SignInAsync(mode)), destructive: true);
            else Run(() => SignInAsync(mode));
        };
        return button;
    }

    async Task<string> SignInAsync(AuthMode mode)
    {
        if (!await _workspace.CloseAllAsync(true))
            return "Sign-in cancelled: wait for active tab operations to finish.";
        _session.Cancel();
        _session.Dispose();
        _session = new CancellationTokenSource();
        var ct = _session.Token;
        await _queues.ResetAsync();
        _credentials.Reset(mode);
        _tree.Items.Clear();
        _tenantOf.Clear();
        _index.Clear();
        _signedIn = false;
        _account.Text = "Signing in…";
        var tenants = await _explorer.GetTenantsAsync();
        _pinned = new TreeViewItem { Header = Ui.Label(Ui.Glyph("★", G.Pinned), "Pinned"), IsExpanded = true };
        _tree.Items.Add(_pinned);
        foreach (var tenant in tenants)
        {
            var tenantId = tenant.Data.TenantId?.ToString();
            var node = LazyTreeNode.Create(
                Ui.Label(Ui.Glyph("TN", G.Tenant), tenant.Data.DisplayName ?? tenant.Data.DefaultDomain ?? "Tenant", tenant.Data.DefaultDomain), null,
                async () =>
                {
                    _ = _index.IndexTenantAsync(tenantId, ct); // browser sign-in: index a tenant once it has been unlocked here
                    return (await _explorer.GetSubscriptionsAsync(tenantId)).Select(sub => SubscriptionNode(sub, tenantId, ct));
                });
            _tenantOf[node] = tenantId ?? "";
            if (mode == AuthMode.AzureCli && tenantId != null)
            {
                var login = new MenuItem { Header = "Copy 'az login' command for this tenant" };
                login.Click += async (_, _) =>
                {
                    await Ui.CopyAsync(this, $"az login --tenant {tenantId}");
                    _status.Text = $"Copied: az login --tenant {tenantId}. Run it in a terminal, then right-click the tenant and choose Refresh.";
                };
                node.ContextMenu?.Items.Add(login);
            }
            _tree.Items.Add(node);
        }
        _signedIn = true;
        // The CLI already holds tokens for each tenant, so index everything in the background straight away.
        if (mode == AuthMode.AzureCli)
            foreach (var tenant in tenants) _ = _index.IndexTenantAsync(tenant.Data.TenantId?.ToString(), ct);
        _settings.LastAuthMode = mode;
        SaveSettings();
        RebuildPins();
        _account.Text = $"{(mode == AuthMode.AzureCli ? "Azure CLI" : "Browser")} · {tenants.Count} tenant{(tenants.Count == 1 ? "" : "s")}";
        return mode == AuthMode.AzureCli
            ? $"Signed in. Indexing resources in the background; use {Shortcut("P")} or the explorer search to jump to any queue or app."
            : $"Signed in. Expand a tenant to index it for {Shortcut("P")} search, or right-click resources to pin them.";
    }

    // ---------------------------------------------------------------- explorer

    TreeViewItem SubscriptionNode(SubscriptionResource subscription, string? tenantId, CancellationToken ct) =>
        LazyTreeNode.Create(Ui.Label(Ui.Glyph("S", G.Subscription), subscription.Data.DisplayName), null, async () =>
        {
            Task<List<WebAppRef>>? apps = null;
            async Task<IEnumerable<TreeViewItem>> AppNodes(bool functions)
            {
                if (apps == null || apps.IsFaulted || apps.IsCanceled)
                    apps = _appLogs.GetAppsAsync(subscription.Id.SubscriptionId!, _credentials.For(tenantId), ct);
                return (await apps).Where(app => app.IsFunction == functions).Select(app => AppNode(app, tenantId));
            }
            return new[]
            {
                LazyTreeNode.Create(Ui.Label(Ui.Glyph("SB", G.ServiceBus), "Service Bus"), null, async () =>
                    (await _explorer.GetNamespacesAsync(subscription, tenantId)).Select(ns => NamespaceNode(ns, tenantId))),
                LazyTreeNode.Create(Ui.Label(Ui.Glyph("AS", G.WebApp), "App Services"), null, () => AppNodes(false)),
                LazyTreeNode.Create(Ui.Label(Ui.Glyph("FN", G.FunctionApp), "Function Apps"), null, () => AppNodes(true)),
            };
        });

    TreeViewItem AppNode(WebAppRef app, string? tenantId)
    {
        TreeViewItem Page(string glyph, string color, string title, AppPageKind kind) =>
            PinnableLeaf(Ui.Label(Ui.Glyph(glyph, color), title), new AppPage(app, kind), tenantId);
        return new TreeViewItem
        {
            Header = Ui.Label(Ui.Glyph(app.IsFunction ? "F" : "W", app.IsFunction ? G.FunctionApp : G.WebApp), app.Name, AppDetail(app)),
            Tag = new AppPage(app, AppPageKind.Overview),
            Items =
            {
                Page("O", G.Overview, "Overview", AppPageKind.Overview),
                Page("L", G.Logs, "Logs", AppPageKind.Logs),
                Page("C", G.Config, "Configuration", AppPageKind.Configuration),
                Page("D", G.Deployments, "Deployments", AppPageKind.Deployments),
            },
        };
    }

    /// <summary>Muted tree detail for an app: "linux", "stopped" or both.</summary>
    static string? AppDetail(WebAppRef app)
    {
        var parts = new List<string>();
        if (app.IsLinux) parts.Add("linux");
        if (app.IsStopped) parts.Add("stopped");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    void OpenOverview(WebAppRef app) => _status.Text = $"Overview for {app.Name} is not available in this build.";

    TreeViewItem NamespaceNode(NamespaceRef ns, string? tenantId)
    {
        var node = LazyTreeNode.Create(Ui.Label(Ui.Glyph("NS", G.ServiceBus), ns.Name), ns, async () =>
        {
            var queues = ResourceExplorer.GetQueuesAsync(ns);
            var topics = ResourceExplorer.GetTopicsAsync(ns);
            await Task.WhenAll(queues, topics);
            return (await queues).Select(q => EntityNode(new QueueRef(ns, q.Data.Name), q.Data.CountDetails, tenantId))
                .Concat((await topics).Select(t => TopicNode(ns, t, tenantId)));
        });
        var grant = new MenuItem { Header = "Grant me Service Bus Data Owner" };
        grant.Click += (_, _) => Run(async () =>
            await RoleAssignmentService.GrantDataOwnerToSelfAsync(ns)
                ? $"Granted Data Owner on {ns.Name}. RBAC can take a few minutes."
                : $"You already have Data Owner on {ns.Name}.");
        node.ContextMenu?.Items.Add(grant);
        return node;
    }

    TreeViewItem TopicNode(NamespaceRef ns, ServiceBusTopicResource topic, string? tenantId)
    {
        var entity = new QueueRef(ns, topic.Data.Name, IsTopic: true);
        var node = LazyTreeNode.Create(EntityLabel(entity, null), entity, async () =>
            (await ResourceExplorer.GetSubscriptionsAsync(topic))
                .Select(s => EntityNode(entity with { Subscription = s.Data.Name, IsTopic = false }, s.Data.CountDetails, tenantId)));
        AddPinItem(node, entity, tenantId);
        return node;
    }

    TreeViewItem EntityNode(QueueRef entity, MessageCountDetails? counts, string? tenantId) =>
        PinnableLeaf(EntityLabel(entity, counts), entity, tenantId);

    static Control EntityLabel(QueueRef entity, MessageCountDetails? counts) => entity.Kind switch
    {
        EntityKind.Queue => Ui.Label(Ui.Glyph("Q", G.Queue), entity.Name, CountText(counts)),
        EntityKind.Topic => Ui.Label(Ui.Glyph("T", G.Topic), entity.Name, "topic"),
        _ => Ui.Label(Ui.Glyph("SU", G.SubscriptionEntity), entity.Subscription!, CountText(counts)),
    };

    TreeViewItem PinnableLeaf(Control header, object tag, string? tenantId)
    {
        var node = new TreeViewItem { Header = header, Tag = tag, ContextMenu = new ContextMenu() };
        AddPinItem(node, tag, tenantId);
        return node;
    }

    void AddPinItem(TreeViewItem node, object tag, string? tenantId)
    {
        var pin = tag switch
        {
            QueueRef entity => PinResolver.For(entity, tenantId),
            AppPage page => PinResolver.For(page, tenantId),
            _ => null,
        };
        if (pin == null) return;
        node.ContextMenu ??= new ContextMenu();
        var item = new MenuItem();
        void Update() => item.Header = IsPinned(pin) ? "Unpin" : "Pin";
        node.ContextMenu.Opening += (_, _) => Update();
        item.Click += (_, _) => TogglePin(pin);
        Update();
        node.ContextMenu.Items.Insert(0, item);
    }

    bool IsPinned(Pin pin) => _settings.Pins.Any(p => p.Key == pin.Key);

    void TogglePin(Pin pin)
    {
        var existing = _settings.Pins.FirstOrDefault(p => p.Key == pin.Key);
        if (existing != null) _settings.Pins.Remove(existing);
        else _settings.Pins.Add(pin);
        SaveSettings();
        RebuildPins();
        _status.Text = existing != null ? $"Unpinned {PinResolver.Describe(pin)}." : $"Pinned {PinResolver.Describe(pin)}.";
    }

    void RebuildPins()
    {
        if (_pinned == null) return;
        _pinned.Items.Clear();
        foreach (var pin in _settings.Pins.OrderBy(PinResolver.Describe, StringComparer.OrdinalIgnoreCase))
        {
            var node = new TreeViewItem { Header = PinLabel(pin), Tag = pin, ContextMenu = new ContextMenu() };
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) => TogglePin(pin);
            node.ContextMenu.Items.Add(unpin);
            _pinned.Items.Add(node);
        }
        if (_settings.Pins.Count == 0)
            _pinned.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "Right-click a queue, topic or app page to pin it", Opacity = Ui.Muted, FontSize = 12 } });
        ApplyFilter();
    }

    static Control PinLabel(Pin pin) => pin.Kind switch
    {
        "queue" => Ui.Label(Ui.Glyph("Q", G.Queue), PinResolver.Describe(pin)),
        "topic" => Ui.Label(Ui.Glyph("T", G.Topic), PinResolver.Describe(pin)),
        "subscription" => Ui.Label(Ui.Glyph("SU", G.SubscriptionEntity), PinResolver.Describe(pin)),
        "config" => Ui.Label(Ui.Glyph("C", G.Config), PinResolver.Describe(pin)),
        "deployments" => Ui.Label(Ui.Glyph("D", G.Deployments), PinResolver.Describe(pin)),
        "overview" => Ui.Label(Ui.Glyph("O", G.Overview), PinResolver.Describe(pin)),
        _ => Ui.Label(Ui.Glyph("L", G.Logs), PinResolver.Describe(pin)),
    };

    void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = $"Settings could not be saved: {ex.Message}"; }
    }

    void ApplyFilter()
    {
        UpdateSearchResults();
        var filter = _filter.Text?.Trim() ?? "";
        bool Visit(TreeViewItem node)
        {
            var childMatch = false;
            foreach (var child in node.Items.OfType<TreeViewItem>())
                childMatch |= Visit(child);
            var match = filter.Length == 0 || HeaderText(node).Contains(filter, StringComparison.OrdinalIgnoreCase);
            node.IsVisible = filter.Length == 0 || match || childMatch;
            if (filter.Length > 0 && childMatch) node.IsExpanded = true;
            return match || childMatch;
        }
        foreach (var node in _tree.Items.OfType<TreeViewItem>()) Visit(node);
    }

    static string HeaderText(TreeViewItem node) => node.Header switch
    {
        string text => text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        TextBlock block => block.Text ?? "",
        _ => "",
    };

    // ---------------------------------------------------------------- opening tabs

    void OpenSelected()
    {
        if (_busy || _closing || _tree.SelectedItem is not TreeViewItem { Tag: { } tag }) return;
        switch (tag)
        {
            case QueueRef entity: OpenEntity(entity); break;
            case AppPage page: OpenPage(page); break;
            case Pin pin: Run(async () =>
            {
                var resolved = await _pins.ResolveAsync(pin, _session.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    if (resolved is QueueRef q) OpenEntity(q);
                    else if (resolved is AppPage p) OpenPage(p);
                });
                return $"Opened {PinResolver.Describe(pin)}.";
            }); break;
        }
    }

    void OpenEntity(QueueRef entity)
    {
        var key = $"{entity.KindLabel}:{entity.ResourcePath}";
        _workspace.Open(key, () =>
        {
            var view = new QueueView(entity, _queues, UpdateCounts, this);
            _ = view.RefreshCountsAsync();
            var title = entity.Kind == EntityKind.Topic ? $"{entity.Name} · topic" : $"{entity.DisplayName} · {entity.Namespace.Name}";
            return new WorkspaceDocument(key, title, entity.ResourcePath, view, view.CloseAsync);
        });
    }

    void OpenPage(AppPage page)
    {
        var app = page.App;
        switch (page.Kind)
        {
            case AppPageKind.Configuration:
                _workspace.Open("config:" + app.Id, () =>
                {
                    var view = new AppConfigView(app, _appConfig, this);
                    _ = view.LoadAsync();
                    return new WorkspaceDocument("config:" + app.Id, $"{app.Name} · config", app.Id, view, view.CloseAsync);
                });
                break;
            case AppPageKind.Deployments:
                OpenDeployments(app);
                break;
            case AppPageKind.Overview:
                OpenOverview(app);
                break;
            default:
                _workspace.Open("logs:" + app.Id, () =>
                {
                    var view = new AppLogsView(_appLogs, this);
                    _ = view.SelectAsync(app);
                    return new WorkspaceDocument("logs:" + app.Id, $"{app.Name} · logs", app.Id, view, view.CloseAsync);
                });
                break;
        }
    }

    void OpenDeployments(WebAppRef app) =>
        _workspace.Open("deployments:" + app.Id, () =>
        {
            var view = new DeploymentsView(app, _deployments, this);
            _ = view.LoadAsync();
            return new WorkspaceDocument("deployments:" + app.Id, $"{app.Name} · deployments", app.Id, view, view.CloseAsync);
        });

    void OnIndexChanged()
    {
        var count = _index.Entities.Count + _index.Apps.Count;
        _indexInfo.Text = _index.IsIndexing ? $"Indexing… {count:N0} resources"
            : count == 0 ? "" : $"{count:N0} resources indexed{(_index.Failures > 0 ? $" · {_index.Failures} not accessible" : "")}";
        if (!string.IsNullOrWhiteSpace(_filter.Text)) UpdateSearchResults();
    }

    void UpdateSearchResults()
    {
        var query = _filter.Text?.Trim() ?? "";
        _resultItems = query.Length == 0 ? [] : ResourceItems()
            .Select(i => (Item: i, Score: QuickOpen.Score(query, i.Title + " " + i.Detail))).Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase).Take(50).Select(x => x.Item).ToList();
        _results.ItemsSource = _resultItems.Select(i => new ListBoxItem
        {
            Padding = new Thickness(14, 2),
            Content = new DockPanel
            {
                Children =
                {
                    Ui.Dock(new TextBlock { Text = i.Detail, Opacity = Ui.Muted, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, Avalonia.Controls.Dock.Right),
                    Ui.Label(i.Glyph, i.Title),
                },
            },
        }).ToList();
        var any = _resultItems.Count > 0;
        _results.IsVisible = any;
        _resultsHeader.IsVisible = query.Length > 0;
        _resultsHeader.Text = any ? $"MATCHES ({_resultItems.Count}{(_resultItems.Count == 50 ? "+" : "")})"
            : _index.IsIndexing ? "NO MATCHES YET — STILL INDEXING" : "NO MATCHES";
        _resultsHeader.FontSize = 11;
        _resultsHeader.Opacity = 0.75;
        _resultsHeader.Margin = new Thickness(16, 4, 16, 2);
    }

    /// <summary>Every known resource page: tree nodes, the background index and open tabs, de-duplicated.</summary>
    IEnumerable<QuickOpenItem> ResourceItems()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        QuickOpenItem? Entity(QueueRef entity, string? subscription) => seen.Add(entity.ResourcePath)
            ? new QuickOpenItem(entity.DisplayName, $"{entity.KindLabel} · {entity.Namespace.Name}{(subscription != null ? " · " + subscription : "")}",
                EntityGlyph(entity), () => OpenEntity(entity))
            : null;
        IEnumerable<QuickOpenItem> Pages(WebAppRef app, string? subscription)
        {
            foreach (var kind in new[] { AppPageKind.Overview, AppPageKind.Logs, AppPageKind.Configuration, AppPageKind.Deployments })
            {
                if (!seen.Add(kind + app.Id)) continue;
                var page = new AppPage(app, kind);
                yield return new QuickOpenItem($"{app.Name} · {kind.ToString().ToLowerInvariant()}",
                    (app.IsFunction ? "function app" : "app service") + (subscription != null ? " · " + subscription : ""),
                    Ui.Glyph(kind switch { AppPageKind.Configuration => "C", AppPageKind.Deployments => "D", AppPageKind.Overview => "O", _ => "L" },
                        kind switch { AppPageKind.Configuration => G.Config, AppPageKind.Deployments => G.Deployments, AppPageKind.Overview => G.Overview, _ => G.Logs }),
                    () => OpenPage(page));
            }
        }
        foreach (var node in Nodes(_tree.Items))
        {
            if (node.Tag is QueueRef entity && Entity(entity, null) is { } item) yield return item;
            else if (node.Tag is AppPage page && node.Items.Count > 0)
                foreach (var p in Pages(page.App, null)) yield return p;
        }
        foreach (var indexed in _index.Entities.ToList())
            if (Entity(indexed.Entity, indexed.Subscription) is { } item) yield return item;
        foreach (var indexed in _index.Apps.ToList())
            foreach (var p in Pages(indexed.App, indexed.Subscription)) yield return p;
    }

    IEnumerable<QuickOpenItem> QuickOpenItems()
    {
        foreach (var item in ResourceItems()) yield return item;
        if (_signedIn)
            foreach (var pin in _settings.Pins)
                yield return new QuickOpenItem(PinResolver.Describe(pin), $"pinned {pin.Kind}", Ui.Glyph("★", G.Pinned), () =>
                {
                    _tree.SelectedItem = null;
                    Run(async () =>
                    {
                        var resolved = await _pins.ResolveAsync(pin, _session.Token);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (resolved is QueueRef q) OpenEntity(q);
                            else if (resolved is AppPage p) OpenPage(p);
                        });
                        return $"Opened {PinResolver.Describe(pin)}.";
                    });
                });
        QuickOpenItem Command(string title, string detail, Action run) => new(title, detail, Ui.Glyph(">", G.Tenant), run, IsCommand: true);
        yield return Command("Sign in with Azure CLI", "account", () => Run(() => SignInAsync(AuthMode.AzureCli)));
        yield return Command("Sign in with browser", "account", () => Run(() => SignInAsync(AuthMode.InteractiveBrowser)));
        yield return Command("Forget remembered browser sign-ins", "account", () =>
        {
            _credentials.ForgetBrowserSignIns();
            _status.Text = "Remembered browser accounts removed. Sign in again to choose an account.";
        });
        yield return Command(_settings.AllowUnencryptedTokenCache
                ? "Stop remembering browser sign-in without a keyring"
                : "Remember browser sign-in without a keyring (unencrypted)", "account", () =>
        {
            _settings.AllowUnencryptedTokenCache = !_settings.AllowUnencryptedTokenCache;
            SaveSettings();
            _status.Text = _settings.AllowUnencryptedTokenCache
                ? "Where no OS keyring exists, browser tokens will be stored unencrypted in your profile folder. Sign in again to apply."
                : "Browser tokens will only be stored in an OS keyring. Sign in again to apply.";
        });
        yield return Command("Split editor right", "view", () => _workspace.MoveActive(1, true));
        yield return Command("Split editor down", "view", _workspace.SplitActiveDown);
        yield return Command("Move editor left", "view", () => _workspace.MoveActive(-1));
        yield return Command("Move editor right", "view", () => _workspace.MoveActive(1));
        yield return Command("Toggle sidebar", Shortcut("B"), ToggleSidebar);
        yield return Command("Collapse explorer", "view", () =>
        {
            foreach (var node in Nodes(_tree.Items)) node.IsExpanded = false;
        });
        yield return Command("Close all tabs", "view", () => _ = _workspace.CloseAllAsync());
        yield return Command("Refresh resource index", "search", () =>
        {
            if (!_signedIn) return;
            var ct = _session.Token;
            foreach (var tenant in _tenantOf.Values.Distinct())
                _ = _index.IndexTenantAsync(tenant.Length == 0 ? null : tenant, ct, refresh: true);
        });
        yield return Command("Filter explorer", "view", () =>
        {
            if (!_sidebar.IsVisible) ToggleSidebar();
            _filter.Focus();
        });
    }

    static Control EntityGlyph(QueueRef entity) => entity.Kind switch
    {
        EntityKind.Queue => Ui.Glyph("Q", G.Queue),
        EntityKind.Topic => Ui.Glyph("T", G.Topic),
        _ => Ui.Glyph("SU", G.SubscriptionEntity),
    };

    // ---------------------------------------------------------------- IWorkbench

    public IReadOnlyList<QueueRef> KnownEntities =>
        Nodes(_tree.Items).Select(n => n.Tag).OfType<QueueRef>()
            .Concat(_index.Entities.Select(e => e.Entity))
            .Concat(_workspace.Documents.Select(d => d.View).OfType<QueueView>().Select(v => v.Queue))
            .DistinctBy(q => q.ResourcePath, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<AppConfigView> OpenConfigurations =>
        _workspace.Documents.Select(d => d.View).OfType<AppConfigView>().ToList();

    public void FindInLogs(string operationId, Control anchor)
    {
        var tabs = _workspace.Documents.Where(d => d.View is AppLogsView { App: not null }).ToList();
        void Search(WorkspaceDocument document)
        {
            _workspace.Select(document);
            _ = ((AppLogsView)document.View).FindOperationAsync(operationId);
            Notify($"Searching {document.Title} for operation {operationId}.");
        }
        var open = tabs.Select(d => ((AppLogsView)d.View).App!.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = _index.Apps.Where(a => !open.Contains(a.App.Id))
            .OrderByDescending(a => a.App.IsFunction).ThenBy(a => a.App.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (tabs.Count == 1 && others.Count == 0)
        {
            Search(tabs[0]);
            return;
        }
        var choices = tabs.Select(d => (Label: $"open tab · {d.Title}", Document: (WorkspaceDocument?)d, App: (WebAppRef?)null))
            .Concat(others.Select(a => (Label: $"{(a.App.IsFunction ? "function app" : "app service")} · {a.App.Name} · {a.Subscription}",
                Document: (WorkspaceDocument?)null, App: (WebAppRef?)a.App)))
            .ToList();
        if (choices.Count == 0)
        {
            Notify($"No apps found yet{(_index.IsIndexing ? " (still indexing)" : "")}. Open the consuming app's Logs tab, then use Find in logs again. Operation: {operationId}");
            return;
        }
        Ui.Pick(anchor, "Search which app's Application Insights?", choices, c => c.Label, choice =>
        {
            if (choice.Document != null)
            {
                Search(choice.Document);
                return;
            }
            OpenPage(new AppPage(choice.App!, AppPageKind.Logs));
            if (_workspace.Documents.FirstOrDefault(d => d.Key == "logs:" + choice.App!.Id) is { } opened) Search(opened);
        });
    }

    public void Notify(string message) => _status.Text = message;

    // ---------------------------------------------------------------- counts

    static string CountText(MessageCountDetails? counts) =>
        counts == null ? "" : $"{counts.ActiveMessageCount} · {counts.DeadLetterMessageCount} DLQ";

    void UpdateCounts(QueueRef queue, MessageCountDetails? counts)
    {
        if (queue.Kind != EntityKind.Topic)
            foreach (var node in Nodes(_tree.Items).Where(n => n.Tag is QueueRef q && q.ResourcePath == queue.ResourcePath))
                if (node.Header is StackPanel { Children: [_, _, TextBlock detail] })
                    detail.Text = CountText(counts);
        foreach (var view in _workspace.Documents.Select(d => d.View).OfType<QueueView>().Where(v => v.Queue.ResourcePath == queue.ResourcePath))
            view.ShowCounts(counts);
    }

    async Task AutoRefreshAsync()
    {
        if (_busy || _refreshing || _closing) return;
        _refreshing = true;
        var ct = _session.Token;
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var node in VisibleNamespaces(_tree.Items).ToArray())
            {
                var ns = (NamespaceRef)node.Tag!;
                foreach (var resource in await ResourceExplorer.GetQueuesAsync(ns))
                {
                    ct.ThrowIfCancellationRequested();
                    var queue = new QueueRef(ns, resource.Data.Name);
                    UpdateCounts(queue, resource.Data.CountDetails);
                    updated.Add(queue.ResourcePath);
                }
            }
            foreach (var view in _workspace.Documents.Select(d => d.View).OfType<QueueView>())
            {
                ct.ThrowIfCancellationRequested();
                if (!updated.Contains(view.Queue.ResourcePath)) await view.RefreshCountsAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = $"Count refresh failed: {ex.Message}"; }
        finally { _refreshing = false; }
    }

    static IEnumerable<TreeViewItem> Nodes(ItemCollection items) =>
        items.OfType<TreeViewItem>().SelectMany(node => Nodes(node.Items).Prepend(node));

    static IEnumerable<TreeViewItem> VisibleNamespaces(ItemCollection items) =>
        items.OfType<TreeViewItem>().Where(node => node.IsExpanded).SelectMany(node =>
            node.Tag is NamespaceRef ? [node] : VisibleNamespaces(node.Items));

    // ---------------------------------------------------------------- shell operations and closing

    async void Run(Func<Task<string>> operation)
    {
        if (_busy || _closing) return;
        _busy = true;
        _progress.IsVisible = true;
        _tree.IsEnabled = _signIn.IsEnabled = _workspace.IsEnabled = false;
        _status.Text = "Working...";
        try { _status.Text = await operation(); }
        catch (Exception ex)
        {
            _status.Text = $"Operation failed: {ex.Message}";
            if (!_signedIn) _account.Text = "Not signed in";
        }
        finally
        {
            _busy = false;
            _progress.IsVisible = false;
            _tree.IsEnabled = _signIn.IsEnabled = _workspace.IsEnabled = true;
        }
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        // Closing is asynchronous (tabs stop streams, connections close), so cancel this attempt and close ourselves
        // when ready. On macOS this cancel also aborts Cmd+Q / Quit, so QuitApplication() below finishes the quit.
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            var busy = _busy || _workspace.HasBusyDocuments;
            if ((busy || _workspace.HasUnsavedWork) && !await ConfirmQuitAsync(busy)) return;
            _body.IsEnabled = _signIn.IsEnabled = false;
            _status.Text = "Closing…";
            _autoRefresh.Stop();
            _session.Cancel();
            // Never let a slow or stuck tab or connection keep the app open.
            await Bounded(_workspace.CloseAllAsync(true), TimeSpan.FromSeconds(5));
            await Bounded(_queues.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
            _http.Dispose();
            _allowClose = true;
            Close();
            QuitApplication();
        }
        catch (Exception ex) { _status.Text = $"Could not close cleanly: {ex.Message}"; }
        finally
        {
            _closing = false;
            if (!_allowClose) _body.IsEnabled = _signIn.IsEnabled = true;
        }
    }

    static async Task Bounded(Task task, TimeSpan limit)
    {
        try { await task.WaitAsync(limit); }
        catch (Exception) { /* timed out or failed during shutdown: quitting anyway */ }
    }

    static void QuitApplication()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    Task<bool> ConfirmQuitAsync(bool busy) => ConfirmAsync(
        busy ? "Quit while operations are running?" : "Discard unsaved work?",
        (busy
            ? "Some tabs are still working (for example sending, moving or purging messages, or saving configuration). " +
              "Quitting cancels them: a message being moved may end up in both places or stay in the dead-letter queue, and a configuration save may be incomplete."
            : "") +
        (_workspace.HasUnsavedWork
            ? (busy ? "\n\n" : "") + "Some tabs hold unsaved configuration edits or received-and-deleted messages, which will be discarded."
            : ""),
        busy ? "Quit anyway" : "Discard and quit");

    async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = VsCode.SideBar,
        };
        var confirm = Ui.Button(action, () => dialog.Close(true));
        confirm.Classes.Add("danger");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                Ui.Row(Ui.Primary("Keep working", () => dialog.Close(false)), confirm),
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
