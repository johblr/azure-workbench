using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

namespace AzureWorkbench.Views;

/// <summary>Health, key facts, 24h metrics and restart/stop/start for an App Service / Function App.</summary>
public sealed class AppOverviewView : UserControl, IWorkspaceView
{
    readonly WebAppRef _app;
    readonly AppOverviewService _service;
    readonly DeploymentService? _deployments;
    readonly IWorkbench? _workbench;
    readonly CancellationTokenSource _lifetime = new();
    readonly ProgressBar _progress = Ui.Progress();
    readonly TextBlock _status = Ui.StatusLine();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly Button _restart, _stop, _start, _portal, _openSite, _copyUrl;
    readonly StatCard _state = new("State"), _health = new("Health"), _plan = new("Plan"), _runtime = new("Runtime"),
        _lastDeployment = new("Last deployment"), _instances = new("Instances");
    readonly StackPanel _metricsPanel = new();
    readonly StackPanel _detailsPanel = new();
    readonly StackPanel _functionsPanel = new();
    AppOverview? _data;
    bool _loading, _closed;
    TimeSpan _postActionDelay = TimeSpan.FromSeconds(5);

    public bool IsBusy { get; private set; }
    public string? UnsavedWork => null;
    public WebAppRef App => _app;

    public AppOverviewView(WebAppRef app, AppOverviewService service, DeploymentService? deployments = null, IWorkbench? workbench = null)
    {
        _app = app;
        _service = service;
        _deployments = deployments;
        _workbench = workbench;
        _restart = Ui.Button("Restart...", () => ConfirmRestart());
        _stop = Ui.Button("Stop...", () => StopFlyout().ShowAt(_stop!));
        _start = Ui.Button("Start", () => _ = RunActionAsync("Start", _service.StartAsync,
            "Start requested at {0}. The app starts shortly; Refresh to see the state."));
        _portal = Ui.Button("Open in portal", () => _ = LaunchAsync($"https://portal.azure.com/#resource{app.Id}/overview"));
        _openSite = Ui.Button("Open site", () => _ = LaunchAsync(SiteUrl));
        _copyUrl = Ui.Button("Copy URL", () => _ = CopyUrlAsync());
        UpdateButtons();

        var header = Ui.Header($"{(app.IsFunction ? "Function App" : "App Service")} / {app.Name} · overview", app.Id);
        var toolbar = Ui.Toolbar(Ui.Primary("Refresh", () => _ = LoadAsync()), _restart, _stop, _start, _portal, _openSite, _copyUrl);
        var cards = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(16, 4, 16, 8) };
        foreach (var card in new[] { _state, _health, _plan, _runtime, _lastDeployment, _instances })
            if (card != _lastDeployment || deployments != null) cards.Children.Add(card.Root);

        var tabs = new List<(string, Control)>
        {
            ("Metrics (24h)", new ScrollViewer { Content = _metricsPanel }),
            ("Details", new ScrollViewer { Content = _detailsPanel }),
        };
        if (app.IsFunction) tabs.Add(("Functions", new ScrollViewer { Content = _functionsPanel }));

        Content = new DockPanel
        {
            Background = VsCode.Editor,
            Children =
            {
                Ui.Dock(_progress, Avalonia.Controls.Dock.Top),
                Ui.Dock(header, Avalonia.Controls.Dock.Top),
                Ui.Dock(toolbar, Avalonia.Controls.Dock.Top),
                Ui.Dock(cards, Avalonia.Controls.Dock.Top),
                Ui.Dock(_status, Avalonia.Controls.Dock.Bottom),
                Ui.Tabs(tabs.ToArray()),
            },
        };

        _timer.Tick += (_, _) =>
        {
            if (_closed || IsBusy || _loading || !IsEffectivelyVisible) return;
            _ = RefreshStateAsync();
        };
        AttachedToVisualTree += (_, _) => { if (!_closed) _timer.Start(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    string SiteUrl => _data?.Site.Value is { DefaultHostName.Length: > 0 } s ? "https://" + s.DefaultHostName : "";

    public async Task LoadAsync()
    {
        if (_loading || _closed) return;
        _loading = true;
        _progress.IsVisible = true;
        _status.Text = "Loading overview...";
        try
        {
            var overview = _service.LoadAsync(_app, _lifetime.Token);
            var deployment = LatestDeploymentAsync();
            await Task.WhenAll(overview, deployment);
            if (_closed) return;
            _data = await overview;
            Render(await deployment);
            _status.Text = $"Loaded at {DateTime.Now:HH:mm:ss}.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _status.Text = $"Overview error: {ex.Message}"; }
        finally
        {
            _loading = false;
            if (!_closed) _progress.IsVisible = IsBusy;
        }
    }

    /// <summary>Cancels everything in flight. Refuses while a restart/stop/start request is being sent.</summary>
    public Task CloseAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the restart, stop or start request to finish before closing this tab.");
        _closed = true;
        _timer.Stop();
        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    async Task<Deployment?> LatestDeploymentAsync()
    {
        if (_deployments == null) return null;
        try
        {
            return (await _deployments.ListAsync(_app, _lifetime.Token)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Reloads only state and health (auto-refresh and after start/stop).</summary>
    async Task RefreshStateAsync()
    {
        if (_data == null || _closed) return;
        try
        {
            var site = _service.GetSiteAsync(_app, _lifetime.Token);
            var health = _service.GetHealthAsync(_app, _lifetime.Token);
            await Task.WhenAll(site, health);
            if (_closed || _data == null) return;
            _data = _data with { Site = await site, Health = await health };
            UpdateCards();
            UpdateButtons();
        }
        catch (OperationCanceledException) { }
    }

    // ---- Actions ----

    void ConfirmRestart() => Ui.Confirm(_restart,
        $"Restart {_app.Name}? All instances restart; in-flight requests are dropped and the app is briefly unavailable.",
        "Restart", () => _ = RunActionAsync("Restart", _service.RestartAsync,
            "Restart requested at {0}. Instances restart over the next minute; Refresh to see the state."), destructive: true);

    /// <summary>Confirmation flyout for Stop: the red button is enabled only once the app's exact name is typed.</summary>
    public Flyout StopFlyout()
    {
        var flyout = new Flyout();
        var confirm = new TextBox { Watermark = _app.Name, MinWidth = 280 };
        var stop = Ui.Button("Stop app", () =>
        {
            flyout.Hide();
            _ = RunActionAsync("Stop", _service.StopAsync, "Stop requested at {0}. The app stops shortly; Refresh to see the state.");
        });
        stop.Classes.Add("danger");
        stop.IsEnabled = false;
        confirm.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) stop.IsEnabled = confirm.Text == _app.Name; };
        flyout.Content = new StackPanel
        {
            Spacing = 10, MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"Stop {_app.Name}? The app stops serving requests until it is started again. Type the app name to confirm.",
                },
                confirm,
                Ui.Row(stop, Ui.Button("Cancel", flyout.Hide)),
            },
        };
        return flyout;
    }

    async Task RunActionAsync(string name, Func<WebAppRef, CancellationToken, Task> action, string successFormat)
    {
        if (IsBusy || _closed) return;
        IsBusy = true;
        _progress.IsVisible = true;
        UpdateButtons();
        _status.Text = $"{name} requested...";
        var succeeded = false;
        try
        {
            await action(_app, _lifetime.Token);
            succeeded = true;
            _status.Text = string.Format(CultureInfo.InvariantCulture, successFormat, DateTime.Now.ToString("HH:mm:ss"));
            _workbench?.Notify($"{name} requested for {_app.Name}");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _status.Text = $"{name} failed: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            if (!_closed) _progress.IsVisible = _loading;
            UpdateButtons();
        }
        if (succeeded && !_closed && name != "Restart") _ = RefreshSoonAsync();
    }

    async Task RefreshSoonAsync()
    {
        try
        {
            await Task.Delay(_postActionDelay, _lifetime.Token);
            await RefreshStateAsync();
        }
        catch (OperationCanceledException) { }
    }

    async Task LaunchAsync(string url)
    {
        if (url.Length == 0) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
            else _status.Text = "Cannot open a browser from here.";
        }
        catch (Exception ex) { _status.Text = $"Could not open {url}: {ex.Message}"; }
    }

    async Task CopyUrlAsync()
    {
        if (SiteUrl.Length == 0) return;
        var ok = await Ui.CopyAsync(this, SiteUrl);
        _status.Text = ok ? "Site URL copied." : "No clipboard is available.";
        if (ok) _workbench?.Notify("Site URL copied");
    }

    void UpdateButtons()
    {
        var site = _data?.Site.Value;
        _restart.IsEnabled = !IsBusy && site is { IsRunning: true };
        _stop.IsEnabled = !IsBusy && site is { IsRunning: true };
        _start.IsEnabled = !IsBusy && site is { IsStopped: true };
        _portal.IsEnabled = true;
        _openSite.IsEnabled = _copyUrl.IsEnabled = SiteUrl.Length > 0;
    }

    // ---- Rendering ----

    void Render(Deployment? latest)
    {
        UpdateCards(latest);
        UpdateButtons();
        RenderMetrics();
        RenderDetails();
        RenderFunctions();
    }

    static IBrush StateBrush(string state) => state.ToLowerInvariant() switch
    {
        "running" or "available" => VsCode.Success,
        "stopped" or "unavailable" => VsCode.Danger,
        "degraded" => VsCode.Warning,
        _ => VsCode.SectionHeader,
    };

    void UpdateCards(Deployment? latest = null)
    {
        var d = _data;
        if (d == null) return;
        if (d.Site.Value is { } site)
        {
            var state = site.State.Length > 0 ? site.State : "Unknown";
            _state.Set(state, StateBrush(state), site.UsageState.Length > 0 && site.UsageState != "Normal" ? site.UsageState : "");
        }
        else _state.Set("Unknown", VsCode.SectionHeader, d.Site.Error ?? "");

        if (d.Health.Value is { } health)
            _health.Set(health.AvailabilityState, StateBrush(health.AvailabilityState), health.Summary, health.Summary);
        else _health.Set("Unknown", VsCode.SectionHeader, d.Health.Error ?? "", d.Health.Error);

        if (d.Plan.Value is { } plan)
            _plan.Set(plan.SkuName.Length > 0 ? plan.SkuName + (plan.Capacity is { } c ? $" × {c}" : "") : plan.Name, null,
                string.Join(" · ", new[] { plan.Tier, plan.Name }.Where(t => t.Length > 0)));
        else _plan.Set("Unknown", null, d.Plan.Error ?? "");

        _runtime.Set(d.Config.Value is { Runtime.Length: > 0 } cfg ? cfg.Runtime : d.Config.Ok ? "Unknown" : "Unavailable", null, d.Config.Error ?? "");

        if (_deployments != null && (latest != null || !_lastDeployment.HasValue))
        {
            if (latest == null) _lastDeployment.Set("None recorded", null, "");
            else
                _lastDeployment.Set(latest.StatusText, latest.IsSuccess ? VsCode.Success : latest.IsFailed ? VsCode.Danger
                    : latest.InProgress ? VsCode.Warning : VsCode.SectionHeader,
                    latest.Start is { } t ? Relative(t) : "");
        }

        if (d.Instances.Value is { } instances)
            _instances.Set(instances.Count.ToString(CultureInfo.InvariantCulture), null, "", string.Join("\n", instances.Select(i => i.Name)));
        else _instances.Set("Unknown", null, d.Instances.Error ?? "");
    }

    void RenderMetrics()
    {
        _metricsPanel.Children.Clear();
        var metrics = _data!.Metrics;
        if (metrics.Error != null) _metricsPanel.Children.Add(ErrorText($"Metrics unavailable: {metrics.Error}"));
        if (metrics.Value is not { } list) return;
        if (list.Count == 0) _metricsPanel.Children.Add(Ui.Hint("No metric data was returned for the last 24 hours."));
        var wrap = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(8, 8) };
        foreach (var m in list) wrap.Children.Add(MetricCard(m));
        _metricsPanel.Children.Add(wrap);
    }

    static Control MetricCard(MetricSeries m)
    {
        var errors = m.Name.StartsWith("Http5xx", StringComparison.OrdinalIgnoreCase) && m.Summary is > 0;
        var value = new TextBlock
        {
            Text = FormatValue(m.Summary, m.Unit), FontSize = 18, FontWeight = FontWeight.SemiBold,
            Foreground = errors ? VsCode.Danger : VsCode.EditorText,
        };
        var spark = new Sparkline { Values = m.Points.Select(p => p.Value).ToList(), Margin = new Thickness(0, 6, 0, 0) };
        if (errors) spark.Stroke = VsCode.Danger;
        return new Border
        {
            Background = VsCode.SideBar, CornerRadius = new CornerRadius(4), Padding = new Thickness(12), MinWidth = 180,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = m.DisplayName, Opacity = Ui.Muted, FontSize = 11 },
                    value,
                    new TextBlock { Text = m.Aggregation == "Total" ? "total, 24h" : "average, 24h", Opacity = Ui.Muted, FontSize = 10 },
                    spark,
                },
            },
        };
    }

    void RenderDetails()
    {
        _detailsPanel.Children.Clear();
        var d = _data!;
        foreach (var error in new[] { d.Site.Error, d.Config.Error, d.Slots.Error })
            if (error != null) _detailsPanel.Children.Add(ErrorText(error));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16, 8) };
        var site = d.Site.Value;
        var cfg = d.Config.Value;
        void Row(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = grid.RowDefinitions.Count - 1;
            grid.Children.Add(Ui.At(new TextBlock { Text = label, Opacity = Ui.Muted, Margin = new Thickness(0, 3, 24, 3), VerticalAlignment = VerticalAlignment.Top }, row));
            grid.Children.Add(Ui.At(new SelectableTextBlock { Text = value.Length == 0 ? "-" : value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) }, row, 1));
        }
        if (site != null)
        {
            Row("Host names", string.Join("\n", site.HostNames));
            Row("Location", site.Location);
            Row("Kind", site.Kind);
            Row("HTTPS only", YesNo(site.HttpsOnly));
            Row("ARR affinity", YesNo(site.ClientAffinityEnabled));
        }
        if (cfg != null)
        {
            Row("Always On", YesNo(cfg.AlwaysOn));
            Row("FTPS state", cfg.FtpsState);
            Row("Min TLS version", cfg.MinTlsVersion);
            Row("HTTP/2", YesNo(cfg.Http20Enabled));
            Row("Health check path", cfg.HealthCheckPath);
            Row("Workers", cfg.NumberOfWorkers?.ToString(CultureInfo.InvariantCulture) ?? "");
            Row("32-bit worker", YesNo(cfg.Use32BitWorkerProcess));
        }
        if (site != null)
        {
            Row("Last modified", site.LastModifiedUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "");
            Row("Outbound IPs", string.Join(", ", site.OutboundIpAddresses));
        }
        if (d.Slots.Value is { } slots)
            Row("Slots", string.Join("\n", slots.Select(s => s.State.Length > 0 ? $"{s.Name} · {s.State}" : s.Name)));
        _detailsPanel.Children.Add(grid);
    }

    void RenderFunctions()
    {
        _functionsPanel.Children.Clear();
        if (!_app.IsFunction || _data!.Functions is not { } functions) return;
        if (functions.Error != null) _functionsPanel.Children.Add(ErrorText($"Functions unavailable: {functions.Error}"));
        if (functions.Value is not { } list) return;
        if (list.Count == 0) _functionsPanel.Children.Add(Ui.Hint("No functions found."));
        foreach (var f in list)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,260,200,*"), Margin = new Thickness(16, 3) };
            grid.Children.Add(Ui.At(f.IsDisabled ? Ui.Glyph("−", VsCode.Glyphs.Pinned) : Ui.Glyph("●", VsCode.Glyphs.Deployments), col: 0));
            grid.Children.Add(Ui.At(new TextBlock { Text = f.Name, TextTrimming = TextTrimming.CharacterEllipsis }, col: 1));
            grid.Children.Add(Ui.At(new TextBlock { Text = f.Trigger, Opacity = Ui.Muted }, col: 2));
            grid.Children.Add(Ui.At(new TextBlock { Text = f.IsDisabled ? "Disabled" : "Enabled", Opacity = Ui.Muted }, col: 3));
            _functionsPanel.Children.Add(grid);
        }
    }

    static TextBlock ErrorText(string text) => new()
    {
        Text = text, Foreground = VsCode.Danger, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(16, 6),
    };

    static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => "" };

    // ---- Formatting ----

    internal static string FormatValue(double? value, string unit)
    {
        if (value is not { } v) return "-";
        var c = CultureInfo.InvariantCulture;
        switch (unit)
        {
            case "Seconds":
                return v < 1 ? $"{(v * 1000).ToString("N0", c)} ms" : $"{v.ToString("N2", c)} s";
            case "Milliseconds":
                return v >= 1000 ? $"{(v / 1000).ToString("N2", c)} s" : $"{v.ToString("N0", c)} ms";
            case "Bytes":
                return v >= 1024.0 * 1024 * 1024 ? $"{(v / (1024.0 * 1024 * 1024)).ToString("N2", c)} GB" : $"{(v / (1024.0 * 1024)).ToString("N1", c)} MB";
            case "Percent":
                return $"{v.ToString("N1", c)} %";
            case "Count":
                return v.ToString("N0", c);
            default:
                return v.ToString("N2", c);
        }
    }

    static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    /// <summary>A status-strip card: caption, coloured dot + value, optional second line.</summary>
    sealed class StatCard
    {
        readonly Ellipse _dot = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        readonly TextBlock _value = new() { Text = "...", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _sub = new() { Opacity = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, IsVisible = false };
        public Border Root { get; }
        public bool HasValue { get; private set; }
        public string Text => _value.Text ?? "";

        public StatCard(string title)
        {
            Root = new Border
            {
                Background = VsCode.SideBar, CornerRadius = new CornerRadius(4), Padding = new Thickness(12), MinWidth = 180,
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10, Opacity = Ui.Muted },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _dot, _value } },
                        _sub,
                    },
                },
            };
        }

        public void Set(string value, IBrush? dot, string sub, string? tooltip = null)
        {
            HasValue = true;
            _value.Text = value;
            _dot.IsVisible = dot != null;
            if (dot != null) _dot.Fill = dot;
            _sub.Text = sub;
            _sub.IsVisible = sub.Length > 0;
            ToolTip.SetTip(Root, string.IsNullOrEmpty(tooltip) ? null : tooltip);
        }
    }
}
