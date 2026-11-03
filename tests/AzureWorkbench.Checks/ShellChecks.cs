using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AzureWorkbench.Services;
using AzureWorkbench.Views;

static class ShellChecks
{
    public static void Run()
    {
        var finderPath = AzureWorkbench.Program.WithToolPaths("/usr/bin:/bin:/usr/sbin:/sbin", "/Users/me", macOS: true);
        Check(finderPath.StartsWith("/usr/bin:/bin:/usr/sbin:/sbin:") && finderPath.Contains("/opt/homebrew/bin")
            && finderPath.Contains("/Users/me/.local/bin"), "Finder-launched macOS apps can find Homebrew's az");
        Check(AzureWorkbench.Program.WithToolPaths("/opt/homebrew/bin:/usr/bin", "/Users/me", macOS: true).Split(':').Count(p => p == "/opt/homebrew/bin") == 1,
            "existing PATH entries are kept once, in their original order");
        Check(QuickOpen.Score("", "anything") > 0, "empty quick-open query matches everything");
        Check(QuickOpen.Score("ordq", "orders-queue") > 0 && QuickOpen.Score("xyz", "orders-queue") == 0, "quick open matches subsequences only");
        Check(QuickOpen.Score("orders", "orders · queue") > QuickOpen.Score("orders", "dead-letter for old reorders"),
            "contiguous, early, word-start matches rank first");
        Check(QuickOpen.Score("ORD", "orders") == QuickOpen.Score("ord", "orders"), "quick open is case-insensitive");

        var dir = Path.Combine(Path.GetTempPath(), "awb-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(dir);
            Check(store.Load().Pins.Count == 0 && store.Load().LastAuthMode == null, "missing settings load as defaults");
            var settings = new WorkbenchSettings { LastAuthMode = AuthMode.InteractiveBrowser };
            settings.Pins.Add(new Pin("subscription", "/subscriptions/s/resourceGroups/rg/providers/Microsoft.ServiceBus/namespaces/ns", "orders", "tenant", "audit"));
            store.Save(settings);
            var loaded = store.Load();
            Check(loaded.LastAuthMode == AuthMode.InteractiveBrowser && loaded.Pins.Single().Key == settings.Pins[0].Key
                && File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("InteractiveBrowser"), "settings round-trip with readable enum names");
            Check(PinResolver.Describe(loaded.Pins[0]) == "orders/audit", "subscription pins describe topic/subscription");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ broken");
            Check(store.Load().Pins.Count == 0, "corrupt settings never stop the app starting");
            File.WriteAllText(Path.Combine(dir, "auth-tenant.json"), "{}");
            new CredentialProvider(dir).ForgetBrowserSignIns();
            Check(!File.Exists(Path.Combine(dir, "auth-tenant.json")), "forgetting browser sign-ins removes remembered account records");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", Path.Combine(Path.GetTempPath(), "awb-shell-" + Guid.NewGuid().ToString("N")));
        var shell = new MainWindow();
        shell.Show();
        PumpJobs();
        var tree = Field<TreeView>(shell, "_tree");
        var parent = new TreeViewItem { Header = "orders-namespace", Items = { new TreeViewItem { Header = "payments" }, new TreeViewItem { Header = "orders-dlq-replay" } } };
        tree.Items.Add(parent);
        Field<TextBox>(shell, "_filter").Text = "replay";
        PumpJobs();
        var children = parent.Items.OfType<TreeViewItem>().ToList();
        Check(parent.IsVisible && parent.IsExpanded && !children[0].IsVisible && children[1].IsVisible,
            "explorer filter shows matches with their parents and hides the rest");
        Field<TextBox>(shell, "_filter").Text = "";
        PumpJobs();
        Check(children.All(c => c.IsVisible), "clearing the explorer filter restores every node");
        var quick = Field<QuickOpen>(shell, "_quickOpen");
        shell.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        PumpJobs();
        Check(quick.IsVisible, "Ctrl+P opens quick open");
        shell.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        PumpJobs();
        Check(!quick.IsVisible, "Escape closes quick open");
        var sidebar = Field<Control>(shell, "_sidebar");
        shell.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
        PumpJobs();
        Check(!sidebar.IsVisible, "Ctrl+B hides the sidebar");
        shell.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.Control);
        PumpJobs();
        Check(sidebar.IsVisible, "Ctrl+B shows the sidebar again");
        shell.Close();
        Until(() => !shell.IsVisible);
        Environment.SetEnvironmentVariable("AZUREWORKBENCH_HOME", null);
        Console.WriteLine("Shell checks passed.");
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {name}");
        Console.WriteLine("PASS: " + name);
    }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void PumpJobs() => Dispatcher.UIThread.RunJobs();
    static void Until(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            PumpJobs();
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Shell check timed out");
            Thread.Sleep(5);
        }
    }
}
