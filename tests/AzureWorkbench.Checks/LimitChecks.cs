using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

/// <summary>Checks for the resource index, explorer search, Find in logs fallback, nested deployment logs and purge confirmation.</summary>
static class LimitChecks
{
    const string SiteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders-fn";

    public static void Run()
    {
        // Nested Kudu deployment logs: one metadata lookup, per-item failures don't fail the batch, never leave the SCM host.
        var handler = new Handler();
        var deployments = new DeploymentService(new HttpClient(handler));
        var app = new WebAppRef(SiteId, "orders-fn", "functionapp,linux", new TestCredential(), null);
        var details = Pump(deployments.GetLogDetailsAsync(app, "abc", ["build", "missing"], default));
        Check(details["build"].Contains("Running oryx build") && details["missing"].StartsWith("(details unavailable")
            && handler.Requests.Count(r => r.Host == "management.azure.com") == 1
            && handler.Requests.Where(r => r.Host != "management.azure.com").All(r => r.Host == "orders-fn.scm.azurewebsites.net"),
            "nested deployment logs are batched with one metadata lookup and isolated failures");
        handler.ScmHost = "orders-fn.scm.evil.example";
        handler.Requests.Clear();
        Check(Pump(deployments.GetLogDetailsAsync(app, "abc", ["build"], default)).Count == 0
            && handler.Requests.All(r => r.Host == "management.azure.com"), "nested logs are never fetched from untrusted hosts");

        // Explorer search and Find in logs use the background index.
        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", Path.Combine(Path.GetTempPath(), "awb-limits-" + Guid.NewGuid().ToString("N")));
        var shell = new MainWindow();
        shell.Show();
        PumpJobs();
        var index = Field<ResourceIndex>(shell, "_index");
        var apps = (List<IndexedApp>)typeof(ResourceIndex).GetField("_apps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(index)!;
        apps.Add(new IndexedApp(app, "tenant", "Production"));
        apps.Add(new IndexedApp(app with { Id = SiteId.Replace("orders-fn", "billing-web"), Name = "billing-web", Kind = "app" }, "tenant", "Production"));
        Field<TextBox>(shell, "_filter").Text = "ordfn";
        PumpJobs();
        var results = Field<ListBox>(shell, "_results");
        var items = (List<QuickOpenItem>)typeof(MainWindow).GetField("_resultItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        Check(results.IsVisible && items.Count == 4 && items.All(i => i.Title.StartsWith("orders-fn"))
            && items.Any(i => i.Title.EndsWith("deployments")) && items.Any(i => i.Title.EndsWith("overview")), "explorer search finds indexed apps that are not loaded in the tree");
        Field<TextBox>(shell, "_filter").Text = "";
        PumpJobs();
        Check(!results.IsVisible, "clearing the search hides the results");
        var quick = (IEnumerable<QuickOpenItem>)typeof(MainWindow).GetMethod("QuickOpenItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
        Check(quick.Count(i => i.Title == "billing-web · logs") == 1, "quick open includes indexed apps exactly once");
        var known = ((IWorkbench)shell).OpenConfigurations;
        Check(known.Count == 0, "no configuration tabs are reported when none are open");
        ((IWorkbench)shell).FindInLogs("4bf92f3577b34da6a3ce929d0e0e4736", Field<TextBox>(shell, "_filter"));
        PumpJobs();
        Check(!Field<TextBlock>(shell, "_status").Text!.Contains("No apps found"), "Find in logs offers indexed apps when no Logs tab is open");
        shell.Close();
        Until(() => !shell.IsVisible);
        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", null);

        // Purge requires typing the exact entity name.
        var arm = new ArmClient(new TestCredential(), "11111111-1111-1111-1111-111111111111",
            new ArmClientOptions { Transport = new HttpClientTransport(new HttpClient(new Handler())) });
        var ns = Pump(arm.GetServiceBusNamespaceResource(new ResourceIdentifier(
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/contoso")).GetAsync()).Value;
        var view = new QueueView(new QueueRef(new NamespaceRef(new TestCredential(), ns), "orders"), new QueueService(), (_, _) => { });
        var flyout = (Flyout)typeof(QueueView).GetMethod("PurgeFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        var panel = (StackPanel)flyout.Content!;
        var typed = panel.Children.OfType<TextBox>().Single();
        var purge = ((StackPanel)panel.Children.Last()).Children.OfType<Button>().First();
        Check(!purge.IsEnabled, "purge starts disabled");
        typed.Text = "order";
        Check(!purge.IsEnabled, "a partial name does not enable purge");
        typed.Text = "ORDERS";
        Check(!purge.IsEnabled, "the name must match exactly, including case");
        typed.Text = "orders";
        Check(purge.IsEnabled && purge.Classes.Contains("danger"), "typing the exact name enables the red purge button");
        // Azure CLI tokens: cached, shared between concurrent callers, refreshed near expiry, friendly errors.
        var cli = new CountingCredential();
        var cached = new CachingCredential(cli, new SemaphoreSlim(1), "tenant-b");
        var armScope = new Azure.Core.TokenRequestContext(["https://management.azure.com/.default"]);
        var parallel = Enumerable.Range(0, 20).Select(_ => cached.GetTokenAsync(armScope, default).AsTask()).ToArray();
        cli.Release.SetResult();
        Pump(Task.WhenAll(parallel));
        Check(cli.Calls == 1 && parallel.All(t => t.Result.Token == "token-1"), "20 concurrent requests share one az token fetch");
        Pump(cached.GetTokenAsync(armScope, default).AsTask());
        Check(cli.Calls == 1, "a valid cached token is reused");
        Pump(cached.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://api.applicationinsights.io/.default"]), default).AsTask());
        Check(cli.Calls == 2, "each scope gets its own token");
        cli.Lifetime = TimeSpan.FromMinutes(2);
        var shortLived = new CachingCredential(cli, null, "tenant-b");
        Pump(shortLived.GetTokenAsync(armScope, default).AsTask());
        Pump(shortLived.GetTokenAsync(armScope, default).AsTask());
        Check(cli.Calls == 4, "tokens close to expiry are refreshed");
        cli.Fail = true;
        var failing = new CachingCredential(cli, null, "tenant-c");
        var error = Pump(Task.Run(async () =>
        {
            try { await failing.GetTokenAsync(armScope, default); return null; }
            catch (Azure.Identity.AuthenticationFailedException ex) { return ex; }
        }));
        Check(error != null && error.Message.Contains("az login --tenant tenant-c"), "a tenant without a CLI sign-in says which az login to run");
        cli.Fail = false;
        Pump(failing.GetTokenAsync(armScope, default).AsTask());
        Check(cli.Calls >= 6, "a failed fetch is retried next time rather than cached");

        // Closing never hangs on a tab whose cleanup doesn't finish.
        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", Path.Combine(Path.GetTempPath(), "awb-quit-" + Guid.NewGuid().ToString("N")));
        var quitting = new MainWindow();
        quitting.Show();
        Field<WorkspaceTabs>(quitting, "_workspace").Open("stuck", () =>
            new WorkspaceDocument("stuck", "Stuck", "", new TextBox(), () => new TaskCompletionSource().Task));
        PumpJobs();
        var timer = Stopwatch.StartNew();
        quitting.Close();
        Until(() => !quitting.IsVisible);
        Check(timer.Elapsed < TimeSpan.FromSeconds(9), "the window closes even when a tab's cleanup never completes");
        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", null);
        Console.WriteLine("Limit checks passed.");
    }

    sealed class CountingCredential : TokenCredential
    {
        public int Calls;
        public bool Fail;
        public TimeSpan Lifetime = TimeSpan.FromHours(1);
        public TaskCompletionSource Release { get; } = new();
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            await Release.Task;
            if (Fail) throw new Azure.Identity.CredentialUnavailableException("ERROR: AADSTS50076 ... run az login");
            return new AccessToken($"token-{call}", DateTimeOffset.UtcNow + Lifetime);
        }
    }

    sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string ScmHost { get; set; } = "orders-fn.scm.azurewebsites.net";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.AbsolutePath.Contains("/namespaces/"))
                return Ok($$$"""{"id":"{{{uri.AbsolutePath}}}","name":"contoso","type":"Microsoft.ServiceBus/namespaces","location":"westeurope","properties":{"serviceBusEndpoint":"https://contoso.servicebus.windows.net/"}}""");
            if (uri.Host == "management.azure.com")
                return Ok($$$"""{"properties":{"hostNameSslStates":[{"hostType":"Repository","name":"{{{ScmHost}}}"}]}}""");
            if (uri.AbsolutePath.EndsWith("/log/build"))
                return Ok("""[{"log_time":"2026-11-03T09:12:05Z","message":"Running oryx build..."}]""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
        static Task<HttpResponseMessage> Ok(string json) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static T Pump<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Limit check timed out");
            Thread.Sleep(5);
        }
    }
}
