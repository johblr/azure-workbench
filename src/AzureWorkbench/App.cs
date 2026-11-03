using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using AzureWorkbench.Views;

namespace AzureWorkbench;

public class App : Application
{
    public override void Initialize()
    {
        Name = "Azure Workbench"; // macOS application menu title
        VsCode.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += (_, _) => window.StartAutoSignIn();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
