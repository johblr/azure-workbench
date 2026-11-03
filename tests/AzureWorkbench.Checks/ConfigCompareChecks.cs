using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class ConfigCompareChecks
{
    sealed class Arm : HttpMessageHandler
    {
        public List<string> Puts { get; } = [];
        public string Settings = """{"A":"1","B":"two"}""";
        public string Connections = """{"Db":{"value":"Server=x","type":2}}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put) lock (Puts) Puts.Add(path);
            string result;
            if (path.EndsWith("/config/appsettings/list")) result = Settings;
            else if (path.EndsWith("/config/connectionstrings/list")) result = Connections;
            else if (path.EndsWith("/config/slotConfigNames")) result = """{"appSettingNames":[],"connectionStringNames":[]}""";
            else throw new InvalidOperationException("Unexpected " + request.Method + " " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"properties\":" + result + "}", Encoding.UTF8, "application/json"),
            });
        }
    }

    sealed class FakeWorkbench : IWorkbench
    {
        public List<AppConfigView> Views { get; } = [];
        public IReadOnlyList<QueueRef> KnownEntities => [];
        public IReadOnlyList<AppConfigView> OpenConfigurations => Views;
        public void FindInLogs(string operationId, Control anchor) { }
        public void Notify(string message) { }
    }

    public static void Run()
    {
        Compare();
        Env();
        View();
    }

    static ConfigCompareRow Row(AppConfiguration l, AppConfiguration r, string name) =>
        ConfigComparison.Compare(l, r).Single(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static void Compare()
    {
        var left = new AppConfiguration(
            [new("Same", "1"), new("Diff", "a"), new("Mine", "m"), new("Casing", "v"), new("Slot", "s"), new("Empty", "")],
            [new("Db", "x", "SQLAzure"), new("Tp", "x", "SQLAzure"), new("Only", "o")]);
        var right = new AppConfiguration(
            [new("Same", "1"), new("Diff", "b"), new("Theirs", "t"), new("CASING", "v"), new("Slot", "s", true), new("Empty", "")],
            [new("DB", "x", "SQLAzure"), new("Tp", "x", "Custom")]);
        var rows = ConfigComparison.Compare(left, right);
        Check(Row(left, right, "Same").State == CompareState.Same, "compare: identical setting is Same");
        Check(Row(left, right, "Diff") is { State: CompareState.Different, LeftValue: "a", RightValue: "b" }, "compare: differing value is Different with both values");
        Check(Row(left, right, "Mine") is { State: CompareState.OnlyLeft, RightValue: null }, "compare: setting only in left is OnlyLeft");
        Check(Row(left, right, "Theirs") is { State: CompareState.OnlyRight, LeftValue: null, RightValue: "t" }, "compare: setting only in right is OnlyRight");
        Check(Row(left, right, "Casing").State == CompareState.Same, "compare: names are matched case-insensitively");
        Check(Row(left, right, "Slot") is { State: CompareState.Different, LeftSlot: false, RightSlot: true }, "compare: slot flag difference is Different");
        Check(Row(left, right, "Empty").State == CompareState.Same, "compare: empty values match");
        Check(rows.Single(r => r.Section == "Connection string" && r.Name == "Db").State == CompareState.Same, "compare: connection strings match case-insensitively");
        Check(rows.Single(r => r.Name == "Tp") is { State: CompareState.Different, LeftType: "SQLAzure", RightType: "Custom" }, "compare: connection string type difference is Different");
        Check(rows.Single(r => r.Name == "Only") is { Section: "Connection string", State: CompareState.OnlyLeft }, "compare: connection string only in left");
        Check(rows.Count(r => r.Section == "Setting") == 7 && rows.Count(r => r.Section == "Connection string") == 3, "compare: rows are split by section");
        Check(ConfigComparison.Compare(new AppConfiguration([], []), new AppConfiguration([], [])).Count == 0, "compare: two empty configurations give no rows");
    }

    static void Env()
    {
        Check(ConfigComparison.ToEnv([new("A", "1"), new("B", "")]) == "A=1\nB=\n", "env: plain and empty values are unquoted");
        Check(ConfigComparison.ToEnv([new("A", "hello world")]) == "A=\"hello world\"\n", "env: spaces are quoted");
        Check(ConfigComparison.ToEnv([new("A", "x#y")]) == "A=\"x#y\"\n", "env: hash is quoted");
        Check(ConfigComparison.ToEnv([new("A", "say \"hi\" \\ ok")]) == "A=\"say \\\"hi\\\" \\\\ ok\"\n", "env: quotes and backslashes are escaped inside quotes");
        Check(ConfigComparison.ToEnv([new("A", "l1\nl2")]) == "A=\"l1\\nl2\"\n", "env: newlines are quoted and escaped");
        Check(ConfigComparison.ToEnv([new("P", "C:\\dir")]) == "P=C:\\dir\n", "env: unquoted values keep backslashes");
        Check(ConfigComparison.ToEnv([]) == "", "env: no settings gives empty text");
    }

    static void View()
    {
        const string prefix = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/";
        var armA = new Arm { Settings = """{"A":"1","B":"two"}""", Connections = """{"Db":{"value":"Server=a","type":2}}""" };
        var armB = new Arm
        {
            Settings = """{"a":"1","B":"other","C":"new"}""",
            Connections = """{"Db":{"value":"Server=b","type":2},"Cache":{"value":"r","type":"RedisCache"}}""",
        };
        using var httpA = new HttpClient(armA) { Timeout = Timeout.InfiniteTimeSpan };
        using var httpB = new HttpClient(armB) { Timeout = Timeout.InfiniteTimeSpan };
        var workbench = new FakeWorkbench();
        var appA = new WebAppRef(prefix + "orders", "orders", "app,linux", new TestCredential(), null);
        var appB = new WebAppRef(prefix + "billing", "billing", "functionapp,linux", new TestCredential(), null);
        var a = new AppConfigView(appA, new AppConfigService(httpA), workbench);
        var b = new AppConfigView(appB, new AppConfigService(httpB), workbench);
        var window = new Window { Width = 1200, Height = 800, Content = a };
        window.Show();

        // Only this view is open, and it is not loaded yet: nothing to compare with.
        workbench.Views.Add(a);
        var chooser = typeof(AppConfigView).GetMethod("ChooseCompare", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Pump(a.LoadAsync());
        chooser.Invoke(a, null);
        Check(Field<TextBlock>(a, "_status").Text == "Open another app's Configuration tab first.",
            "compare: with no other open configuration the status asks to open one");

        workbench.Views.Add(b);
        chooser.Invoke(a, null);
        Check(Field<TextBlock>(a, "_status").Text == "Open another app's Configuration tab first.",
            "compare: an unloaded other view is not offered");
        Pump(b.LoadAsync());
        PumpJobs();

        Check(!a.HasChanges, "compare view: no changes before copying");
        typeof(AppConfigView).GetMethod("StartCompare", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(a, [b]);
        PumpJobs();
        var tabs = Field<TabControl>(a, "_tabs");
        Check(tabs.Items.Count == 3 && tabs.SelectedIndex == 2, "compare: picking adds and selects the COMPARE tab");

        var panel = Field<object>(a, "_compare");
        var selected = (HashSet<(string, string)>)panel.GetType().GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var refresh = panel.GetType().GetMethod("Refresh")!;
        var copy = (Button)panel.GetType().GetField("_copy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Check(!copy.IsEnabled, "compare: copy button is disabled with nothing selected");
        selected.Add(("Setting", "b"));
        selected.Add(("Setting", "c"));
        selected.Add(("Connection string", "cache"));
        selected.Add(("Setting", "a"));
        refresh.Invoke(panel, null);
        PumpJobs();
        Check(!selected.Contains(("Setting", "a")), "compare: identical rows cannot stay selected");
        Check(copy.IsEnabled, "compare: copy button enables with a selection");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpJobs();

        Check(a.HasChanges, "copy: copying rows marks the view as having changes");
        var current = (AppConfiguration)typeof(AppConfigView).GetMethod("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(a, [false])!;
        Check(current.Settings.Single(s => s.Name == "B").Value == "other", "copy: existing setting is overwritten with the other value");
        Check(current.Settings.Any(s => s.Name == "C" && s.Value == "new"), "copy: missing setting is added");
        Check(current.ConnectionStrings.Any(c => c.Name == "Cache" && c.Value == "r" && c.Type == "RedisCache"), "copy: connection string is added with its type");
        Check(current.ConnectionStrings.Single(c => c.Name == "Db").Value == "Server=a", "copy: unselected rows are untouched");
        Check(a.LoadedConfiguration!.Settings.Single(s => s.Name == "B").Value == "two", "copy: loaded snapshot stays as saved");
        Check(Field<TextBlock>(a, "_status").Text!.StartsWith("Copied 3 rows"), "copy: status reports the number of rows copied");
        Check(armA.Puts.Count == 0 && armB.Puts.Count == 0, "copy: no PUT is issued");
        Check(b.LoadedConfiguration!.Settings.Count == 3 && !b.HasChanges, "copy: the source view is unchanged");
        window.Close();
    }

    static void Check(bool success, string name)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void Pump(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Config compare check timed out");
            Thread.Sleep(5);
        }
        PumpJobs();
    }
}
