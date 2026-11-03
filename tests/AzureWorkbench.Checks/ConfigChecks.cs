using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AzureWorkbench.Models;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class ConfigChecks
{
    const string SiteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders";
    static readonly WebAppRef Linux = new(SiteId, "orders", "app,linux", new TestCredential(), null);
    static readonly WebAppRef Windows = new(SiteId, "orders", "app", new TestCredential(), null);

    sealed record Req(string Method, string Path, string Query, string Body);

    /// <summary>Fake ARM site configuration with request recording.</summary>
    sealed class FakeArm : HttpMessageHandler
    {
        public List<Req> Requests { get; } = [];
        public string Settings = """{"A":"1","B":"two"}""";
        public string Connections = """{"Db":{"value":"Server=x","type":2}}""";
        public string Slot = """{"appSettingNames":["B"],"connectionStringNames":[],"azureStorageConfigNames":["Files"]}""";
        public HttpStatusCode? FailWith;
        public string FailBody = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "management.azure.com") throw new InvalidOperationException("Non-ARM request " + request.RequestUri);
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add(new Req(request.Method.Method, request.RequestUri.AbsolutePath, request.RequestUri.Query, body));
            if (FailWith is { } status)
                return new HttpResponseMessage(status) { Content = new StringContent(FailBody, Encoding.UTF8, "application/json") };
            var path = request.RequestUri.AbsolutePath;
            string Props(string json) => "{\"properties\":" + json + "}";
            string result;
            if (request.Method == HttpMethod.Put)
            {
                var props = JsonDocument.Parse(body).RootElement.GetProperty("properties").GetRawText();
                if (path.EndsWith("/config/appsettings")) Settings = props;
                else if (path.EndsWith("/config/connectionstrings")) Connections = props;
                else if (path.EndsWith("/config/slotConfigNames")) Slot = props;
                result = Props(props);
            }
            else if (path.EndsWith("/config/appsettings/list")) result = Props(Settings);
            else if (path.EndsWith("/config/connectionstrings/list")) result = Props(Connections);
            else if (path.EndsWith("/config/slotConfigNames")) result = Props(Slot);
            else throw new InvalidOperationException("Unexpected " + request.Method + " " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        }
    }

    public static void Run()
    {
        Model();
        Service();
        View();
    }

    static void Model()
    {
        var before = new AppConfiguration(
            [new("A", "1"), new("B", "2"), new("C", "3"), new("Sticky", "s")],
            [new("Db", "x", "SQLAzure")]);
        var after = new AppConfiguration(
            [new("a", "1"), new("B", "22"), new("D", "4"), new("Sticky", "s", true)],
            [new("Db", "x", "SQLAzure")]);
        var changes = Diff.Between(before, after);
        Check(changes.Any(c => c.Kind == ChangeKind.Added && c.Name == "D"), "diff reports added settings");
        Check(changes.Any(c => c.Kind == ChangeKind.Removed && c.Name == "C"), "diff reports removed settings");
        Check(changes.Any(c => c.Kind == ChangeKind.Changed && c.Name == "B" && c.Detail == "value"), "diff reports changed values");
        Check(changes.Any(c => c.Kind == ChangeKind.Changed && c.Name == "Sticky" && c.Detail == "slot setting"), "diff reports slot-setting-only changes");
        Check(!changes.Any(c => c.Kind != ChangeKind.Changed && c.Name.Equals("A", StringComparison.OrdinalIgnoreCase)),
            "diff compares names case-insensitively (no add/remove for casing)");
        Check(Diff.Between(before, before).Count == 0 && before.SameAs(before), "identical configurations have no diff");

        var secret = new AppConfiguration([new("K", "super-secret-value")], []);
        var secretChanges = Diff.Between(new AppConfiguration([new("K", "old-secret-value")], []), secret)
            .Concat(Diff.Between(new AppConfiguration([], []), secret)).Concat(Diff.Between(secret, new AppConfiguration([], [])));
        Check(secretChanges.Any() && secretChanges.All(c => !c.ToString().Contains("secret-value") && !c.Detail.Contains("secret-value")),
            "ConfigChange.ToString never contains setting values");

        var (errors, warnings) = new AppConfiguration([new("", "v"), new("dup", "1"), new("DUP", "2"), new("a=b", "x")],
            [new("Db", "x", "Bogus")]).Validate(false);
        Check(errors.Any(e => e.Contains("blank")), "validate flags blank names");
        Check(errors.Any(e => e.Contains("Duplicate")), "validate flags case-insensitive duplicates");
        Check(errors.Any(e => e.Contains("a=b")), "validate flags names containing '='");
        Check(errors.Any(e => e.Contains("unknown type")), "validate flags unknown connection string types");
        var odd = new AppConfiguration([new("my-setting!", "v")], []);
        Check(odd.Validate(true) is { Errors.Count: 0, Warnings.Count: 1 }, "validate warns (not errors) for odd names on Linux");
        Check(odd.Validate(false) is { Errors.Count: 0, Warnings.Count: 0 }, "validate does not warn for odd names off Linux");

        var settings = new List<AppSetting> { new("Q", "He said \"hi\" \\ é 日本語 😀 <&>", true), new("E", "") };
        Check(AppConfiguration.SettingsFromJson(AppConfiguration.SettingsToJson(settings)).SequenceEqual(settings), "settings JSON round-trips unicode and quotes");
        var conns = new List<ConnectionStringSetting> { new("Db", "Server=\"é\";x='y'", "SQLAzure", true), new("C", "v") };
        Check(AppConfiguration.ConnectionStringsFromJson(AppConfiguration.ConnectionStringsToJson(conns)).SequenceEqual(conns), "connection strings JSON round-trips");
        Check(AppConfiguration.ConnectionStringsFromJson("""[{"name":"x","value":"v","type":"sqlazure"}]""")[0].Type == "SQLAzure",
            "connection string type casing is canonicalized");
        Check(Throws<ArgumentException>(() => AppConfiguration.SettingsFromJson("""{"name":"x"}""")), "FromJson rejects a non-array");
        Check(Throws<ArgumentException>(() => AppConfiguration.SettingsFromJson("""[{"name":"x","value":{"a":1}}]""")), "FromJson rejects nested object values");
        Check(Throws<ArgumentException>(() => AppConfiguration.SettingsFromJson("""[{"name":"x","value":["a"]}]""")), "FromJson rejects nested array values");
    }

    static void Service()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppConfigService(http);
        arm.Slot = """{"appSettingNames":["B"],"connectionStringNames":["Db"],"azureStorageConfigNames":["Files"]}""";
        var loaded = service.GetAsync(Linux, default).GetAwaiter().GetResult();
        Check(arm.Requests.Any(r => r.Method == "POST" && r.Path == SiteId + "/config/appsettings/list"), "GetAsync POSTs appsettings/list");
        Check(arm.Requests.Any(r => r.Method == "POST" && r.Path == SiteId + "/config/connectionstrings/list"), "GetAsync POSTs connectionstrings/list");
        Check(arm.Requests.Any(r => r.Method == "GET" && r.Path == SiteId + "/config/slotConfigNames"), "GetAsync GETs slotConfigNames");
        Check(arm.Requests.All(r => r.Query == "?api-version=2023-12-01"), "GetAsync uses api-version 2023-12-01");
        Check(loaded.Settings.Single(s => s.Name == "B").SlotSetting && !loaded.Settings.Single(s => s.Name == "A").SlotSetting,
            "slot flags come from appSettingNames");
        Check(loaded.ConnectionStrings.Single().SlotSetting, "slot flags come from connectionStringNames");
        Check(loaded.ConnectionStrings.Single().Type == "SQLAzure" && loaded.ConnectionStrings.Single().Value == "Server=x",
            "integer connection string type 2 maps to SQLAzure");

        // value-only change
        arm.Requests.Clear();
        var edited = new AppConfiguration(loaded.Settings.Select(s => s.Name == "A" ? s with { Value = "changed" } : s).ToList(), loaded.ConnectionStrings);
        service.SaveAsync(Linux, loaded, edited, default).GetAwaiter().GetResult();
        var puts = arm.Requests.Where(r => r.Method == "PUT").ToList();
        Check(puts.Count == 1 && puts[0].Path == SiteId + "/config/appsettings", "value-only change PUTs only appsettings");
        Check(puts[0].Body == """{"properties":{"A":"changed","B":"two"}}""", "appsettings PUT body holds all settings under properties");

        // slot-only toggle
        loaded = service.GetAsync(Linux, default).GetAwaiter().GetResult();
        arm.Requests.Clear();
        edited = new AppConfiguration(loaded.Settings.Select(s => s.Name == "A" ? s with { SlotSetting = true } : s).ToList(), loaded.ConnectionStrings);
        service.SaveAsync(Linux, loaded, edited, default).GetAwaiter().GetResult();
        puts = arm.Requests.Where(r => r.Method == "PUT").ToList();
        Check(puts.Count == 1 && puts[0].Path == SiteId + "/config/slotConfigNames", "slot-only toggle PUTs only slotConfigNames");
        using (var body = JsonDocument.Parse(puts[0].Body))
        {
            var props = body.RootElement.GetProperty("properties");
            Check(props.GetProperty("azureStorageConfigNames")[0].GetString() == "Files", "slotConfigNames PUT preserves azureStorageConfigNames");
            Check(props.GetProperty("appSettingNames").EnumerateArray().Select(e => e.GetString()).Order().SequenceEqual(["A", "B"]),
                "slotConfigNames PUT lists the toggled setting");
        }

        // conflict
        loaded = service.GetAsync(Linux, default).GetAwaiter().GetResult();
        arm.Settings = """{"A":"changed-elsewhere","B":"two"}""";
        arm.Requests.Clear();
        Check(Throws<ConfigConflictException>(() => service.SaveAsync(Linux, loaded,
                new AppConfiguration(loaded.Settings.Select(s => s with { Value = "x" }).ToList(), loaded.ConnectionStrings), default).GetAwaiter().GetResult()),
            "save throws ConfigConflictException when Azure changed");
        Check(arm.Requests.All(r => r.Method != "PUT"), "conflict sends no PUT");

        // validation
        arm.Requests.Clear();
        Check(Throws<ArgumentException>(() => service.SaveAsync(Linux, loaded,
                new AppConfiguration([new("X", "1"), new("x", "2")], []), default).GetAwaiter().GetResult()),
            "save throws ArgumentException for validation errors");
        Check(arm.Requests.Count == 0, "validation errors send no request");

        // error surfacing
        arm.FailWith = HttpStatusCode.Forbidden;
        arm.FailBody = """{"error":{"message":"denied by policy"}}""";
        var ex = Catch<HttpRequestException>(() => service.GetAsync(Linux, default).GetAwaiter().GetResult());
        Check(ex != null && ex.Message.Contains("denied by policy"), "403 surfaces HttpRequestException with the Azure message");
    }

    static void View()
    {
        var arm = new FakeArm();
        using var http = new HttpClient(arm) { Timeout = Timeout.InfiniteTimeSpan };
        var view = new AppConfigView(Linux, new AppConfigService(http));
        var notes = new List<string>();
        var workspace = new WorkspaceTabs(notes.Add);
        var window = new Window { Width = 1200, Height = 800, Content = workspace };
        window.Show();
        var doc = workspace.Open("config:test", () => new WorkspaceDocument("config:test", "orders · config", SiteId, view, view.CloseAsync));
        Pump(view.LoadAsync());
        PumpJobs();

        var settings = Field<object>(view, "_settings");
        var valueBoxes = settings.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings) is Control rows
            ? rows.GetLogicalDescendants().OfType<TextBox>().Where(t => t.Watermark as string == "value").ToList() : [];
        Check(valueBoxes.Count == 2, "view shows a value box per setting");
        Check(valueBoxes.All(t => t.PasswordChar != '\0'), "values are masked after load");
        var show = Field<CheckBox>(view, "_showValues");
        show.IsChecked = true;
        PumpJobs();
        Check(valueBoxes.All(t => t.PasswordChar == '\0'), "Show values clears the mask");
        show.IsChecked = false;
        PumpJobs();
        Check(valueBoxes.All(t => t.PasswordChar != '\0'), "unchecking Show values masks again");

        Check(!view.HasChanges && view.UnsavedWork == null, "freshly loaded view has no changes");
        valueBoxes[0].Text = "edited";
        PumpJobs();
        Check(view.HasChanges && view.UnsavedWork != null, "editing a value marks changes and unsaved work");
        Check(!Pump(workspace.CloseDocumentAsync(doc)) && workspace.Documents.Contains(doc), "closing with unsaved edits is refused");

        // advanced mode with invalid JSON
        valueBoxes[0].Text = "1";
        PumpJobs();
        var toggle = Field<Avalonia.Controls.Primitives.ToggleButton>(view, "_advanced");
        toggle.IsChecked = true;
        PumpJobs();
        var json = (TextBox)settings.GetType().GetField("_json", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        json.Text = "[ not valid json";
        PumpJobs();
        toggle.IsChecked = false;
        PumpJobs();
        Check(toggle.IsChecked == true, "invalid JSON keeps advanced mode on");
        Check(json.Text == "[ not valid json", "invalid JSON text is kept");

        Check(Pump(workspace.CloseDocumentAsync(doc, true)), "closing with discardUnsaved succeeds");
        window.Close();
    }

    static bool Throws<T>(Action action) where T : Exception => Catch<T>(action) != null;

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
    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Config check timed out");
            Thread.Sleep(5);
        }
        PumpJobs();
    }
}
