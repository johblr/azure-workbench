using System.Net;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class OverviewChecks
{
    const string SiteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders";
    const string PlanId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan1";
    static readonly WebAppRef Func = new(SiteId, "orders", "functionapp,linux", new TestCredential(), null);
    static readonly WebAppRef Web = new(SiteId, "orders", "app,linux", new TestCredential(), null);
    static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    sealed class FakeArm : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
        public Dictionary<string, (HttpStatusCode Status, string Body)> Overrides { get; } = [];
        public Task? Gate;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add((request.Method, uri));
            if (uri.Host != "management.azure.com") throw new InvalidOperationException("Request sent to " + uri.Host);
            var path = uri.AbsolutePath;
            foreach (var (suffix, response) in Overrides)
                if (path.EndsWith(suffix, StringComparison.Ordinal))
                    return Reply(response.Status, response.Body);
            if (request.Method == HttpMethod.Post)
            {
                if (Gate != null) await Gate;
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            string json;
            if (path == SiteId)
                json = """
                    {"location":"West Europe","kind":"functionapp,linux","properties":{"state":"Running","usageState":"Normal","availabilityState":"Normal",
                     "defaultHostName":"orders.azurewebsites.net","hostNames":["orders.azurewebsites.net","orders.contoso.com"],"httpsOnly":true,
                     "clientAffinityEnabled":false,"serverFarmId":"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan1",
                     "lastModifiedTimeUtc":"2026-01-01T10:00:00Z","outboundIpAddresses":"1.1.1.1,2.2.2.2"}}
                    """;
            else if (path == SiteId + "/config/web")
                json = """{"properties":{"linuxFxVersion":"DOTNET-ISOLATED|8.0","alwaysOn":true,"ftpsState":"FtpsOnly","minTlsVersion":"1.2","http20Enabled":true,"healthCheckPath":"/health","numberOfWorkers":2,"use32BitWorkerProcess":false}}""";
            else if (path == PlanId)
                json = """{"name":"plan1","kind":"linux","sku":{"name":"P1v3","tier":"PremiumV3","capacity":2},"properties":{"numberOfSites":3,"maximumElasticWorkerCount":10}}""";
            else if (path.EndsWith("/availabilityStatuses/current"))
                json = """{"properties":{"availabilityState":"Available","summary":"Healthy","reasonType":"","occuredTime":"2026-01-01T09:00:00Z","detailedStatus":"All good"}}""";
            else if (path == SiteId + "/instances")
                json = """{"value":[{"name":"abc123","properties":{"name":"abc123"}},{"name":"def456"}]}""";
            else if (path == SiteId + "/slots")
                json = """{"value":[{"name":"orders/staging","properties":{"state":"Running","defaultHostName":"orders-staging.azurewebsites.net"}}]}""";
            else if (path == SiteId + "/functions")
                json = """
                    {"value":[
                     {"name":"orders/Process","properties":{"isDisabled":false,"invoke_url_template":"https://x/api/p","config":{"bindings":[{"type":"serviceBusTrigger"},{"type":"blob"}]}}},
                     {"name":"orders/Bare"}]}
                    """;
            else if (path == SiteId + "/deployments")
                json = """{"value":[{"name":"orders/n","properties":{"status":4,"start_time":"2026-01-01T10:00:00Z"}}]}""";
            else if (path.EndsWith("/providers/Microsoft.Insights/metrics"))
                json = Metrics(uri.Query);
            else throw new InvalidOperationException("Unexpected " + uri);
            return Reply(HttpStatusCode.OK, json);
        }

        static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        static string Metrics(string query)
        {
            var names = Uri.UnescapeDataString(query.Split("metricnames=")[1].Split('&')[0]).Split(',');
            var series = names.Select(n => n switch
            {
                "Requests" => Series(n, "Count", "total", ["10", "20", "null", "5"]),
                "Http5xx" => Series(n, "Count", "total", ["0", "2"]),
                "MemoryWorkingSet" => Series(n, "Bytes", "average", ["100", "null", "300"]),
                _ => Series(n, "Count", "total", ["1"]),
            });
            return "{\"value\":[" + string.Join(",", series) + "]}";
        }

        static string Series(string name, string unit, string key, string[] values)
        {
            var data = values.Select((v, i) => $$"""{"timeStamp":"2026-01-01T{{i:00}}:00:00Z"{{(v == "null" ? "" : $",\"{key}\":{v}")}}}""");
            return $$"""{"name":{"value":"{{name}}","localizedValue":"{{name}}"},"unit":"{{unit}}","timeseries":[{"data":[{{string.Join(",", data)}}]}]}""";
        }
    }

    public static void Run()
    {
        Service();
        View();
        SparklineChecks();
    }

    static void Service()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppOverviewService(http);
        var o = Pump(service.LoadAsync(Func, default, Now));

        var site = o.Site.Value!;
        Check(site.State == "Running" && site.IsRunning && site.DefaultHostName == "orders.azurewebsites.net" && site.HostNames.Count == 2
            && site.HttpsOnly == true && site.ClientAffinityEnabled == false && site.Location == "West Europe" && site.Kind == "functionapp,linux"
            && site.OutboundIpAddresses.SequenceEqual(["1.1.1.1", "2.2.2.2"]) && site.LastModifiedUtc != null, "site fields are parsed");
        var cfg = o.Config.Value!;
        Check(cfg.Runtime == "DOTNET-ISOLATED|8.0" && cfg.AlwaysOn == true && cfg.FtpsState == "FtpsOnly" && cfg.MinTlsVersion == "1.2"
            && cfg.Http20Enabled == true && cfg.HealthCheckPath == "/health" && cfg.NumberOfWorkers == 2 && cfg.Use32BitWorkerProcess == false,
            "config fields and Linux runtime are parsed");
        var plan = o.Plan.Value!;
        Check(plan.SkuName == "P1v3" && plan.Tier == "PremiumV3" && plan.Capacity == 2 && plan.NumberOfSites == 3 && plan.MaximumElasticWorkerCount == 10,
            "plan is parsed from the server farm id");
        Check(o.Health.Value!.AvailabilityState == "Available" && o.Health.Value.Summary == "Healthy" && o.Health.Value.OccurredTime.StartsWith("2026"),
            "health is parsed (occuredTime)");
        Check(o.Instances.Value!.Select(i => i.Name).SequenceEqual(["abc123", "def456"]), "instances are parsed");
        Check(o.Slots.Value!.Single() is { Name: "staging", State: "Running", DefaultHostName: "orders-staging.azurewebsites.net" }, "slot name is the last segment");
        var functions = o.Functions!.Value!;
        Check(functions[0] is { Name: "Process", IsDisabled: false, Trigger: "serviceBusTrigger" } && functions[0].InvokeUrl == "https://x/api/p",
            "function name, trigger and invoke URL are parsed");
        Check(functions[1] is { Name: "Bare", Trigger: "", IsDisabled: false }, "functions with missing fields parse leniently");
        Check(o.Metrics.Value!.Count == 4, "function app metrics are loaded");

        arm.Requests.Clear();
        var web = Pump(service.LoadAsync(Web, default, Now));
        Check(web.Functions == null && arm.Requests.All(r => !r.Uri.AbsolutePath.EndsWith("/functions")), "web apps do not query functions");
        Check(web.Metrics.Value!.Select(m => m.Name).SequenceEqual(AppOverviewService.WebMetrics.Select(m => m.Name)), "web app metric names are requested");

        // Lenient parsing
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        var bareSite = AppOverviewService.ParseSite(empty.RootElement);
        Check(bareSite.State == "" && bareSite.HostNames.Count == 0 && bareSite.HttpsOnly == null && bareSite.LastModifiedUtc == null && bareSite.OutboundIpAddresses.Count == 0,
            "missing site fields parse to empty/null");
        var bareConfig = AppOverviewService.ParseConfig(empty.RootElement);
        Check(bareConfig.Runtime == "" && bareConfig.AlwaysOn == null && bareConfig.NumberOfWorkers == null, "missing config fields parse to empty/null");
        Check(AppOverviewService.ParseHealth(empty.RootElement).AvailabilityState == "Unknown", "missing health state is Unknown");
        using var windows = System.Text.Json.JsonDocument.Parse("""{"properties":{"netFrameworkVersion":"v8.0"}}""");
        Check(AppOverviewService.ParseConfig(windows.RootElement).Runtime == "v8.0", "Windows runtime uses netFrameworkVersion");
        Check(AppOverviewService.ParseConfig(windows.RootElement, new Dictionary<string, string> { ["FUNCTIONS_WORKER_RUNTIME"] = "dotnet-isolated" }).Runtime
            == "dotnet-isolated (v8.0)", "worker runtime is used when settings are already available");

        // One failing section does not fail the others
        arm.Overrides["/config/web"] = (HttpStatusCode.Forbidden, """{"error":{"message":"No access"}}""");
        var partial = Pump(service.LoadAsync(Func, default, Now));
        Check(partial.Config.Value == null && partial.Config.Error!.Contains("403") && partial.Config.Error.Contains("Reader on the app"),
            "a 403 section reports its error with the hint");
        Check(partial.Site.Ok && partial.Health.Ok && partial.Metrics.Ok && partial.Plan.Ok, "other sections still load when one is forbidden");
        arm.Overrides.Clear();

        // Metrics request
        arm.Requests.Clear();
        Pump(service.GetMetricsAsync(Func, default, Now));
        var metricsRequest = arm.Requests.Single().Uri;
        Check(metricsRequest.Host == "management.azure.com" && metricsRequest.Scheme == "https", "metrics go only to management.azure.com");
        Check(metricsRequest.Query.Contains("timespan=2026-01-01T03%3A04%3A05Z%2F2026-01-02T03%3A04%3A05Z"), "metrics timespan covers the last 24h and is escaped");
        Check(metricsRequest.Query.Contains("api-version=2023-10-01") && metricsRequest.Query.Contains("interval=PT1H")
            && metricsRequest.Query.Contains("metricnames=FunctionExecutionCount%2CHttp5xx%2CRequests%2CMemoryWorkingSet")
            && metricsRequest.Query.Contains("aggregation=Total,Average"), "metrics query has api-version, interval, names and aggregation");

        // Fallback on 400
        var fallback = new FakeArm();
        using var http2 = new HttpClient(fallback) { Timeout = Timeout.InfiniteTimeSpan };
        var service2 = new AppOverviewService(http2);
        fallback.Overrides["/providers/Microsoft.Insights/metrics"] =
            (HttpStatusCode.BadRequest, """{"error":{"message":"Failed to find metric configuration for provider: Microsoft.Web, metric: HealthCheckStatus"}}""");
        var failed = Pump(service2.GetMetricsAsync(Web, default, Now));
        Check(failed.Error != null && fallback.Requests.Count == 2, "a metric 400 is retried once and then reported");
        Check(fallback.Requests[1].Uri.Query.Contains("metricnames=Requests%2CHttp5xx") && !fallback.Requests[1].Uri.Query.Contains("CpuTime"),
            "the retry asks only for Requests and Http5xx");
        fallback.Overrides.Clear();
        fallback.Requests.Clear();
        var fallbackOk = new FakeArm();
        using var http3 = new HttpClient(new FallbackOnce(fallbackOk)) { Timeout = Timeout.InfiniteTimeSpan };
        var recovered = Pump(new AppOverviewService(http3).GetMetricsAsync(Web, default, Now));
        Check(recovered.Ok && recovered.Value!.Select(m => m.Name).SequenceEqual(["Requests", "Http5xx"]), "fallback metrics are returned after a 400");

        // Summary
        var byName = o.Metrics.Value!.ToDictionary(m => m.Name);
        Check(byName["Requests"].Summary == 35 && byName["Requests"].Aggregation == "Total" && byName["Requests"].Points.Count == 4
            && byName["Requests"].Points[2].Value == null, "Total summary is the sum and nulls stay as gaps");
        Check(byName["MemoryWorkingSet"].Summary == 200 && byName["MemoryWorkingSet"].Aggregation == "Average", "Average summary is the mean of non-null points");
        Check(byName["Http5xx"].DisplayName == "Server errors (5xx)" && byName["Requests"].Unit == "Count", "metric display name and unit are set");

        // Actions
        arm.Requests.Clear();
        Pump(service.RestartAsync(Func, default));
        Pump(service.StopAsync(Func, default));
        Pump(service.StartAsync(Func, default));
        Check(arm.Requests.All(r => r.Method == HttpMethod.Post), "restart, stop and start use POST");
        Check(arm.Requests[0].Uri.AbsolutePath == SiteId + "/restart" && arm.Requests[0].Uri.Query == "?api-version=2023-12-01&softRestart=false&synchronous=false",
            "restart posts to /restart with softRestart=false");
        Check(arm.Requests[1].Uri.AbsolutePath == SiteId + "/stop" && arm.Requests[2].Uri.AbsolutePath == SiteId + "/start", "stop and start post to their paths");
    }

    /// <summary>Answers the first metrics request with a 400, then delegates.</summary>
    sealed class FallbackOnce(FakeArm inner) : HttpMessageHandler
    {
        bool _first = true;
        readonly MethodInfo _send = typeof(HttpMessageHandler).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_first && request.RequestUri!.AbsolutePath.EndsWith("/metrics"))
            {
                _first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"Invalid metric name: CpuTime"}}""", Encoding.UTF8, "application/json"),
                });
            }
            return (Task<HttpResponseMessage>)_send.Invoke(inner, [request, ct])!;
        }
    }

    static void View()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppOverviewService(http);
        var view = new AppOverviewView(Func, service, new DeploymentService(http));
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        Check(view.UnsavedWork == null && !view.IsBusy && view.App == Func, "overview view starts idle with no unsaved work");
        Check(!Field<Button>(view, "_start").IsEnabled && !Field<Button>(view, "_restart").IsEnabled, "actions are disabled before the state is known");
        Pump(view.LoadAsync());
        Check(CardText(view, "_state") == "Running", "State card shows Running");
        Check(CardText(view, "_health") == "Available", "Health card shows Available");
        Check(CardText(view, "_plan").StartsWith("P1v3") && CardText(view, "_runtime") == "DOTNET-ISOLATED|8.0" && CardText(view, "_instances") == "2"
            && CardText(view, "_lastDeployment") == "Success", "plan, runtime, instances and last deployment cards load");
        Check(Field<TextBlock>(view, "_status").Text!.StartsWith("Loaded at "), "status line reports the load time");
        Check(!Field<Button>(view, "_start").IsEnabled, "Start is disabled while the app is running");
        Check(Field<Button>(view, "_restart").IsEnabled && Field<Button>(view, "_stop").IsEnabled, "Restart and Stop are enabled while running");
        Check(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Sparkline>().Count() == 4, "each metric renders a sparkline");

        var flyout = view.StopFlyout();
        var panel = (StackPanel)flyout.Content!;
        var box = Descendants<TextBox>(panel).Single();
        var stop = Descendants<Button>(panel).Single(b => (b.Content as string) == "Stop app");
        Check(!stop.IsEnabled, "the Stop confirmation starts disabled");
        box.Text = "orde";
        Check(!stop.IsEnabled, "a partial name does not enable Stop");
        box.Text = "ORDERS";
        Check(!stop.IsEnabled, "the name must match exactly");
        box.Text = "orders";
        Check(stop.IsEnabled, "the exact app name enables Stop");

        var gate = new TaskCompletionSource();
        arm.Gate = gate.Task;
        var run = typeof(AppOverviewView).GetMethod("RunActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var action = (Task)run.Invoke(view, ["Restart", (Func<WebAppRef, CancellationToken, Task>)service.RestartAsync, "Restart requested at {0}."])!;
        Until(() => view.IsBusy);
        Check(!Field<Button>(view, "_restart").IsEnabled, "actions are disabled while one is in flight");
        Check(Catch<InvalidOperationException>(() => view.CloseAsync().GetAwaiter().GetResult()) != null, "closing during an action throws");
        gate.SetResult();
        Pump(action);
        Check(!view.IsBusy && Field<TextBlock>(view, "_status").Text!.StartsWith("Restart requested at "), "the action result is shown in the status line");
        Pump(view.CloseAsync());
        Check(true, "closing after the action completes succeeds");
        window.Close();

        var denied = new FakeArm();
        denied.Overrides["/config/web"] = (HttpStatusCode.Forbidden, """{"error":{"message":"No access"}}""");
        denied.Overrides["/providers/Microsoft.Insights/metrics"] = (HttpStatusCode.Forbidden, """{"error":{"message":"No monitor"}}""");
        using var http2 = new HttpClient(denied) { Timeout = Timeout.InfiniteTimeSpan };
        var partial = new AppOverviewView(Func, new AppOverviewService(http2));
        Pump(partial.LoadAsync());
        Check(CardText(partial, "_state") == "Running" && CardText(partial, "_runtime") == "Unavailable", "a failing section does not fail the tab");
        Pump(partial.CloseAsync());
    }

    static void SparklineChecks()
    {
        var spark = new Sparkline();
        var window = new Window { Width = 300, Height = 100, Content = spark };
        window.Show();
        foreach (var values in new IReadOnlyList<double?>[]
        {
            [], [null, null, null], [1], [0, 0, 0], [1, 5, null, 3, 8, null, null, 2], [double.NaN, 1, 2],
        })
        {
            spark.Values = values;
            Pump(Task.Delay(10));
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(120, 32));
            bitmap.Render(spark);
        }
        Check(true, "sparkline renders empty, all-null, flat and gapped series");
        Check(spark.Width == Sparkline.DefaultWidth && spark.Height == Sparkline.DefaultHeight, "sparkline defaults to 120x32");
        window.Close();
    }

    static string CardText(object view, string field)
    {
        var card = Field<object>(view, field);
        return (string)card.GetType().GetProperty("Text")!.GetValue(card)!;
    }

    static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();

    static T? Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        return null;
    }

    static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static T Pump<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void Until(Func<bool> done)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Overview check timed out");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
