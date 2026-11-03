using Avalonia;

namespace AzureWorkbench;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Apps opened from Finder/Dock (or a Linux menu) get a minimal PATH without Homebrew or ~/.local/bin,
        // so Azure CLI sign-in could not find `az`. Add the usual install locations for this process only.
        if (!OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("PATH", WithToolPaths(Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsMacOS()));
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // WSLg/X11 popups as separate windows get stuck open; render them inside the main window instead
            .With(new X11PlatformOptions { OverlayPopups = true })
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Appends common CLI install folders (Homebrew, /usr/local, ~/.local/bin) that are missing from <paramref name="path"/>.
    /// Unix-only PATH semantics, so it always joins with '/' and ':' regardless of the host OS.
    /// </summary>
    public static string WithToolPaths(string? path, string home, bool macOS)
    {
        var parts = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        string[] extra = macOS
            ? ["/opt/homebrew/bin", "/usr/local/bin", $"{home.TrimEnd('/')}/.local/bin"]
            : ["/usr/local/bin", $"{home.TrimEnd('/')}/.local/bin", "/home/linuxbrew/.linuxbrew/bin"];
        foreach (var dir in extra)
            if (!parts.Contains(dir)) parts.Add(dir);
        return string.Join(':', parts);
    }
}
