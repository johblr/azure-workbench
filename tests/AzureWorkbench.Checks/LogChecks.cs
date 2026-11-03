using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class LogChecks
{
    const string SiteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders";
    const string ComponentId = "/subscriptions/sub/resourceGroups/rg/providers/microsoft.insights/components/insights";
    static int _count;

    sealed class FakeInsights : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        public TaskCompletionSource? ArmGate;
        public bool HasComponent = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "management.azure.com")
            {
                if (ArmGate != null) await ArmGate.Task;
                return Json(HasComponent
                    ? """{"value":[{"id":"COMPONENT","name":"insights","properties":{"AppId":"app-id"}}]}""".Replace("COMPONENT", ComponentId)
                    : """{"value":[]}""");
            }
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            Queries.Add(document.RootElement.GetProperty("query").GetString()!);
            return Json("""
                {"tables":[{"name":"PrimaryResult",
                "columns":[{"name":"timestamp"},{"name":"severityLevel"},{"name":"cloud_RoleName"},{"name":"message"},{"name":"operation_Id"},{"name":"itemType"}],
                "rows":[["2026-11-02T10:00:00Z",3,"orders","GET /x 500 12ms","op-1","request"]]}]}
                """);
        }
    }

    public static void Run()
    {
        // BuildQuery
        var all = AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.All);
        Check(all.Contains("(traces |") && all.Contains("(exceptions |") && all.Contains("(requests |") && all.Contains("(dependencies |"),
            "query includes all four tables when requested");
        Check(all.Contains("strcat(name, \" \", resultCode") && all.Contains("strcat(type, \" \", target, \" \", name, \" \", resultCode")
            && all.Contains("iff(tobool(success), 1, 3)"), "requests and dependencies build messages and severity from success");
        Check(all.Contains("operation_Id") && all.Contains("itemType") && all.Contains("take 500"), "query projects operation id and item type and keeps the limit");
        var def = AppLogService.BuildQuery(30, 0, "", "");
        Check(def.Contains("(traces |") && def.Contains("(exceptions |") && !def.Contains("(requests |") && !def.Contains("(dependencies |"),
            "default query is traces and exceptions only");
        var reqOnly = AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.Requests);
        Check(reqOnly.Contains("(requests |") && !reqOnly.Contains("(traces |") && !reqOnly.Contains("(exceptions |") && !reqOnly.Contains("(dependencies |"),
            "query includes only the selected tables");
        var depOnly = AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.Dependencies);
        Check(depOnly.Contains("(dependencies |") && !depOnly.Contains("(requests |"), "dependencies can be selected alone");
        Check(Throws<ArgumentException>(() => AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.None)), "at least one table is required");
        Check(Throws<ArgumentOutOfRangeException>(() => AppLogService.BuildQuery(1441, 0, "", "", TelemetryTypes.All))
            && Throws<ArgumentOutOfRangeException>(() => AppLogService.BuildQuery(30, 5, "", "", TelemetryTypes.All)),
            "minutes and severity validation still apply");
        Check(!AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.All, "").Contains("operation_Id =="), "no operation filter when id is blank");
        var op = AppLogService.BuildQuery(1440, 0, "", "", TelemetryTypes.All, "abc-123");
        Check(op.Contains("| where operation_Id == 'abc-123'") && op.Contains("ago(1440m)"), "operation id filter is added");
        var inj = AppLogService.BuildQuery(30, 0, "", "", TelemetryTypes.All, "x' or 1==1 | take 1 //\n\\");
        Check(inj.Contains(@"operation_Id == 'x\' or 1==1 | take 1 //\n\\'"), "operation id injection attempts are escaped as data");
        Check(!inj.Contains("\n| take 1"), "injected newline does not start a new KQL line");

        // QueryAsync
        var handler = new FakeInsights();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppLogService(http);
        var app = new WebAppRef(SiteId, "orders", "app", new TestCredential(), ComponentId);
        var insights = new InsightsRef(ComponentId, "insights", "app-id");
        var entries = service.QueryAsync(app, insights, 60, 0, "", "", default, TelemetryTypes.All, "op-1").GetAwaiter().GetResult();
        Check(entries is [{ OperationId: "op-1", ItemType: "request", Message: "GET /x 500 12ms", Severity: "3" }],
            "QueryAsync parses operation_Id and itemType");
        Check(handler.Queries[^1].Contains("operation_Id == 'op-1'") && handler.Queries[^1].Contains("(dependencies |"),
            "QueryAsync sends the operation filter and selected tables");

        // FindOperationAsync
        var view = new AppLogsView(service);
        var tabs = Field<TabControl>(view, "_tabs");
        Pump(view.SelectAsync(app));
        Check(Field<ComboBox>(view, "_insights").SelectedItem is InsightsRef, "linked Insights resource is selected");
        tabs.SelectedIndex = 1;
        handler.Queries.Clear();
        Pump(view.FindOperationAsync("op-9'x"));
        Check(tabs.SelectedIndex == 0, "FindOperation switches to the Recent tab");
        Check(handler.Queries.Count == 1, "FindOperation runs exactly one query");
        var q = handler.Queries[0];
        Check(q.Contains(@"operation_Id == 'op-9\'x'") && q.Contains("ago(1440m)")
            && q.Contains("(traces |") && q.Contains("(exceptions |") && q.Contains("(requests |") && q.Contains("(dependencies |"),
            "FindOperation queries all tables for the escaped operation id over 24 hours");
        Check(Field<TextBox>(view, "_operation").Text == "op-9'x" && Field<CheckBox>(view, "_requests").IsChecked == true
            && Field<NumericUpDown>(view, "_minutes").Value == 1440, "FindOperation sets operation id, checkboxes and minutes");
        Check(Field<SelectableTextBlock>(view, "_recent").Text!.Contains("[Error] request orders op:op-1\nGET /x 500 12ms"),
            "results show item type, role and operation id");

        // No Insights selected, none available
        handler.HasComponent = false;
        var noInsights = new AppLogsView(service);
        var plain = app with { InsightsResourceId = null };
        Pump(noInsights.SelectAsync(plain));
        handler.Queries.Clear();
        Pump(noInsights.FindOperationAsync("op-2"));
        Check(handler.Queries.Count == 0 && Field<TextBlock>(noInsights, "_status").Text
            == "Select this app's Application Insights resource, then click Refresh recent.", "FindOperation without Insights shows guidance");

        // Waits for in-progress discovery
        handler.HasComponent = true;
        handler.ArmGate = new TaskCompletionSource();
        var waiting = new AppLogsView(service);
        var selecting = waiting.SelectAsync(app);
        var finding = waiting.FindOperationAsync("op-3");
        Until(() => Field<ProgressBar>(waiting, "_progress").IsVisible);
        Check(!finding.IsCompleted && handler.Queries.Count == 0, "FindOperation waits for Insights discovery");
        handler.ArmGate.SetResult();
        Pump(selecting);
        Pump(finding);
        Check(handler.Queries.Count == 1 && handler.Queries[0].Contains("operation_Id == 'op-3'"), "FindOperation queries after discovery completes");

        Console.WriteLine($"{_count} log checks passed.");
    }

    static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + name);
        _count++;
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Log check timed out");
            Thread.Sleep(5);
        }
    }
}
