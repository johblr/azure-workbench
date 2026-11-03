using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AzureWorkbench.Views;

static class Ui
{
    public static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    public const double Muted = 0.55;

    /// <summary>True for the platform's command modifier: Ctrl on Windows/Linux, Cmd on macOS (Ctrl is also accepted there).</summary>
    public static bool IsCommand(KeyModifiers modifiers)
    {
        var command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        return modifiers.HasFlag(command) || modifiers.HasFlag(KeyModifiers.Control);
    }

    /// <summary>Title with optional dimmed detail text, for tree headers.</summary>
    public static Control Label(string title, string? detail = null)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(new TextBlock { Text = title });
        if (detail != null) p.Children.Add(new TextBlock { Text = detail, Opacity = Muted });
        return p;
    }

    /// <summary>Copies text to the system clipboard (Windows, macOS, X11/Wayland). Returns false if no clipboard is available.</summary>
    public static async Task<bool> CopyAsync(Visual visual, string text)
    {
        if (TopLevel.GetTopLevel(visual)?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    public static StackPanel Row(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    // ---- Shared VS Code-style building blocks. Use these in every tab so spacing and type stay consistent. ----

    /// <summary>Standard tab header: title, muted resource line and optional muted guidance.</summary>
    public static StackPanel Header(string title, string? subtitle = null, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(16, 10, 16, 4) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        if (subtitle != null)
            panel.Children.Add(new SelectableTextBlock { Text = subtitle, Opacity = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        if (hint != null) panel.Children.Add(Hint(hint));
        return panel;
    }

    /// <summary>Muted, wrapping guidance text.</summary>
    public static TextBlock Hint(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Opacity = Muted, FontSize = 11, Margin = new Thickness(0, 2),
    };

    /// <summary>VS Code-style section label (e.g. "DETAILS").</summary>
    public static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.75,
        Margin = new Thickness(16, 8, 16, 4), Foreground = VsCode.SectionHeader,
    };

    /// <summary>Wrapping row of tool controls with standard spacing.</summary>
    public static WrapPanel Toolbar(params Control[] controls)
    {
        var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(16, 6) };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    /// <summary>Accent (primary) button.</summary>
    public static Button Primary(string text, Action onClick)
    {
        var b = Button(text, onClick);
        b.Classes.Add("accent");
        return b;
    }

    /// <summary>VS Code panel-style tabs (uppercase headers with an active underline).</summary>
    public static TabControl Tabs(params (string Header, Control Content)[] tabs)
    {
        var control = new TabControl { Margin = new Thickness(8, 0) };
        control.Classes.Add("panel");
        foreach (var (header, content) in tabs)
            control.Items.Add(new TabItem { Header = header.ToUpperInvariant(), Content = content });
        return control;
    }

    /// <summary>Status line at the bottom of a tab.</summary>
    public static TextBlock StatusLine() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 6), FontSize = 12 };

    /// <summary>Thin indeterminate progress bar shown at the top of a busy tab.</summary>
    public static ProgressBar Progress() => new() { IsIndeterminate = true, IsVisible = false, Height = 2, MinHeight = 2 };

    /// <summary>Read-only monospace output (logs, message bodies).</summary>
    public static SelectableTextBlock Output() => new()
    {
        FontFamily = VsCode.Mono, FontSize = 13, Foreground = VsCode.EditorText,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8),
    };

    /// <summary>
    /// Shows a confirmation flyout next to <paramref name="anchor"/>. Destructive actions get a red button.
    /// </summary>
    public static void Confirm(Control anchor, string message, string action, Action confirmed, bool destructive = false)
    {
        var flyout = new Flyout();
        var button = Button(action, () =>
        {
            flyout.Hide();
            confirmed();
        });
        button.Classes.Add(destructive ? "danger" : "accent");
        flyout.Content = new StackPanel
        {
            Spacing = 10, MaxWidth = 460,
            Children =
            {
                new ScrollViewer { MaxHeight = 360, Content = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap } },
                Row(button, Button("Cancel", flyout.Hide)),
            },
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>Shows a pick list flyout; <paramref name="picked"/> receives the chosen item.</summary>
    public static void Pick<T>(Control anchor, string prompt, IEnumerable<T> items, Func<T, string> label, Action<T> picked)
    {
        var flyout = new Flyout();
        var list = new ListBox { MaxHeight = 320, MinWidth = 280 };
        list.ItemsSource = items.Select(i => new ListBoxItem { Content = label(i), Tag = i }).ToList();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: T item }) return;
            flyout.Hide();
            picked(item);
        };
        flyout.Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = prompt }, list } };
        flyout.ShowAt(anchor);
    }

    /// <summary>Small coloured glyph badge used for resource types, in the spirit of VS Code file icons.</summary>
    public static Control Glyph(string glyph, string color) => new Border
    {
        Width = 18, Height = 16, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.Parse(color), 0.18),
        Child = new TextBlock
        {
            Text = glyph, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse(color)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Tree/list label with a glyph badge, title and optional muted detail.</summary>
    public static Control Label(Control glyph, string title, string? detail = null)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(glyph);
        p.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        if (detail != null) p.Children.Add(new TextBlock { Text = detail, Opacity = Muted, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    public static T At<T>(T control, int row = 0, int col = 0) where T : Control
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, col);
        return control;
    }

    public static T Dock<T>(T control, Avalonia.Controls.Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
