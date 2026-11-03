using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class DeploymentChecks
{
    const string SiteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders";
    static readonly WebAppRef App = new(SiteId, "orders", "functionapp,linux", new TestCredential(), null);

    sealed class FakeArm : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string ScmName = "orders.scm.azurewebsites.net";
        public HttpStatusCode? FailWith;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri);
            if (uri.Host.Contains("evil")) throw new InvalidOperationException("Request sent to " + uri.Host);
            if (FailWith is { } status)
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent("""{"error":{"message":"No access"}}""", Encoding.UTF8, "application/json"),
                });
            var path = uri.AbsolutePath;
            string json;
            if (uri.Host == ScmName) json = """[{"log_time":"2026-01-01T10:00:00Z","message":"nested line"}]""";
            else if (path == SiteId + "/deployments" && !uri.Query.Contains("page=2"))
                json = $$$"""
                    {"value":[
                      {"name":"orders/old1","properties":{"status":3,"author":"Ann","deployer":"GitHub","message":"broken","start_time":"2026-01-01T10:00:00Z","end_time":"2026-01-01T10:01:30Z","active":false}},
                      {"name":"orders/new2","properties":{"status":4,"author":"Bob","author_email":"b@x","deployer":"ZipDeploy","message":"ship it\nsecond line","start_time":"2026-01-03T10:00:00Z","end_time":"2026-01-03T10:00:20Z","active":true}}],
                     "nextLink":"https://management.azure.com{{{SiteId}}}/deployments?api-version=2023-12-01&page=2"}
                    """;
            else if (path == SiteId + "/deployments")
                json = """{"value":[{"name":"orders/mid3","properties":{"status":9}},{"name":"orders/bare"}]}""";
            else if (path.EndsWith("/log"))
                json = """
                    {"value":[
                      {"properties":{"log_time":"2026-01-03T10:00:01Z","message":"Updating site","details_url":"https://orders.scm.azurewebsites.net/api/deployments/new2/log/d1"}},
                      {"properties":{"message":"Done"}}]}
                    """;
            else if (path == SiteId)
                json = $$$"""{"properties":{"hostNameSslStates":[{"name":"orders.azurewebsites.net","hostType":"Standard"},{"name":"{{{ScmName}}}","hostType":"Repository"}]}}""";
            else throw new InvalidOperationException("Unexpected " + uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    public static void Run()
    {
        Service();
        View();
    }

    static void Service()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new DeploymentService(http);
        var list = Pump(service.ListAsync(App, default));
        Check(list.Count == 4, "list follows nextLink across pages");
        Check(arm.Requests[0].AbsolutePath == SiteId + "/deployments" && arm.Requests[0].Query.StartsWith("?api-version=2023-12-01"),
            "list requests /deployments with api-version 2023-12-01");
        Check(list[0].Id == "new2" && list[1].Id == "old1", "deployments are sorted newest first");
        Check(list[^1].Start == null, "deployments without a start time sort last");
        var ok = list[0];
        Check(ok.Status == 4 && ok.StatusText == "Success" && ok.Author == "Bob" && ok.Deployer == "ZipDeploy" && ok.Active
            && ok.Duration == TimeSpan.FromSeconds(20), "success deployment fields and duration are parsed");
        Check(list[1].StatusText == "Failed" && !list[1].Active && Deployment.FormatDuration(list[1].Duration) == "1m 30s", "failed status maps");
        Check(list.Single(d => d.Id == "mid3").StatusText == "Unknown (9)", "unknown status codes are reported");
        var bare = list.Single(d => d.Id == "bare");
        Check(bare.Author == "" && bare.Message == "" && bare.Start == null && bare.End == null && bare.Duration == null && !bare.Active,
            "missing fields parse leniently");
        Check(new[] { 0, 1, 2, 3, 4 }.Select(Deployment.StatusName).SequenceEqual(["Pending", "Building", "Deploying", "Failed", "Success"]),
            "status names for 0-4");

        arm.Requests.Clear();
        var log = Pump(service.GetLogAsync(App, "new/2", default));
        Check(arm.Requests[0].AbsoluteUri.Contains("/deployments/new%2F2/log?api-version=2023-12-01"),
            "log request escapes the deployment id and uses api-version");
        Check(log.Count == 2 && log[0].Message == "Updating site" && log[0].DetailsId == "d1" && log[0].Time != null, "log entries are parsed");
        Check(log[1].DetailsId == null && log[1].Time == null, "log entries without details or time parse leniently");

        arm.Requests.Clear();
        var details = Pump(service.GetLogDetailsAsync(App, "new2", "d1", default));
        Check(details != null && details.Contains("nested line") && arm.Requests.Any(r => r.Host == arm.ScmName && r.AbsolutePath == "/api/deployments/new2/log/d1"),
            "details are fetched from the app's SCM host");

        arm.ScmName = "evil.example.com";
        arm.Requests.Clear();
        Check(Pump(service.GetLogDetailsAsync(App, "new2", "d1", default)) == null, "details refuse a non-SCM host");
        Check(arm.Requests.All(r => r.Host == "management.azure.com"), "no request is sent to the non-SCM host");
        arm.ScmName = "orders.scm.evil.com";
        Check(Pump(service.GetLogDetailsAsync(App, "new2", "d1", default)) == null && arm.Requests.All(r => r.Host == "management.azure.com"),
            "details refuse hosts that merely contain .scm.");

        arm.FailWith = HttpStatusCode.Forbidden;
        var ex = Catch<HttpRequestException>(() => Pump(service.ListAsync(App, default)));
        Check(ex != null && ex.Message.Contains("403") && ex.Message.Contains("Reader access") && ex.Message.Contains("No access"),
            "403 surfaces HttpRequestException with hint");
    }

    static void View()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var view = new DeploymentsView(App, new DeploymentService(http));
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        Check(view.UnsavedWork == null && !view.IsBusy, "deployments view starts idle with no unsaved work");
        Pump(view.LoadAsync());
        var list = Field<ListBox>(view, "_list");
        Check(list.ItemCount == 4 && !view.IsBusy, "LoadAsync populates the list");
        Check(Field<TextBlock>(view, "_summary").Text!.StartsWith("4 deployments"), "summary shows the deployment count");
        list.SelectedIndex = 0;
        var output = Field<SelectableTextBlock>(view, "_output");
        Until(() => output.Text != null && output.Text.Contains("Updating site"));
        Check(output.Text!.Contains("new2") && output.Text.Contains("Success"), "selecting a deployment loads its log into the details");
        Check(Field<Button>(view, "_copy").IsEnabled, "copy is enabled once a deployment is selected");
        Pump(view.CloseAsync());
        Check(true, "close cancels without throwing");
        window.Close();

        var denied = new FakeArm { FailWith = HttpStatusCode.Forbidden };
        using var http2 = new HttpClient(denied) { Timeout = Timeout.InfiniteTimeSpan };
        var failing = new DeploymentsView(App, new DeploymentService(http2));
        Pump(failing.LoadAsync());
        Check(Field<TextBlock>(failing, "_status").Text!.Contains("403"), "view shows load errors in the status line");
    }

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
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Deployment check timed out");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
