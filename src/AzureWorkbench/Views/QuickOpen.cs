using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AzureWorkbench.Views;

public sealed record QuickOpenItem(string Title, string Detail, Control Glyph, Action Run, bool IsCommand = false);

/// <summary>VS Code-style quick open (Ctrl/Cmd+P) and command palette (Ctrl/Cmd+Shift+P or a leading '>').</summary>
public sealed class QuickOpen : Border
{
    readonly TextBox _input = new() { Watermark = "Search loaded and pinned resources, or type > for commands", FontSize = 13 };
    readonly ListBox _list = new() { MaxHeight = 380, Background = Brushes.Transparent };
    readonly Func<IEnumerable<QuickOpenItem>> _items;
    List<QuickOpenItem> _shown = [];

    public QuickOpen(Func<IEnumerable<QuickOpenItem>> items)
    {
        _items = items;
        IsVisible = false;
        Width = 620;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 6, 0, 0);
        Background = VsCode.SideBar;
        BorderBrush = VsCode.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(6);
        BoxShadow = BoxShadows.Parse("0 4 16 0 #80000000");
        ZIndex = 100;
        Child = new DockPanel { Children = { Ui.Dock(_input, Avalonia.Controls.Dock.Top), _list } };
        _input.TextChanged += (_, _) => Refresh();
        _input.KeyDown += OnKey;
        _list.DoubleTapped += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        };
    }

    public void Show(bool commands)
    {
        IsVisible = true;
        _input.Text = commands ? ">" : "";
        Refresh();
        _input.Focus();
        _input.CaretIndex = _input.Text?.Length ?? 0;
    }

    public void Hide() => IsVisible = false;

    void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: Accept(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
        }
    }

    void Move(int delta)
    {
        if (_shown.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _shown.Count - 1);
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    void Accept()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _shown.Count) return;
        var item = _shown[_list.SelectedIndex];
        Hide();
        item.Run();
    }

    void Refresh()
    {
        var text = _input.Text ?? "";
        var commands = text.StartsWith('>');
        var query = (commands ? text[1..] : text).Trim();
        _shown = _items().Where(i => i.IsCommand == commands)
            .Select(i => (Item: i, Score: Score(query, i.Title + " " + i.Detail)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(60).Select(x => x.Item).ToList();
        _list.ItemsSource = _shown.Select(i => new ListBoxItem
        {
            Padding = new Thickness(8, 4),
            Content = new DockPanel
            {
                Children =
                {
                    Ui.Dock(new TextBlock { Text = i.Detail, Opacity = Ui.Muted, Margin = new Thickness(12, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, Avalonia.Controls.Dock.Right),
                    Ui.Label(i.Glyph, i.Title),
                },
            },
        }).ToList();
        _list.SelectedIndex = _shown.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Fuzzy subsequence score: 0 when <paramref name="query"/> is not a case-insensitive subsequence of <paramref name="text"/>;
    /// higher for contiguous runs, word-start matches and earlier matches. An empty query matches everything.
    /// </summary>
    public static int Score(string query, string text)
    {
        if (query.Length == 0) return 1;
        var score = 0;
        var run = 0;
        var t = 0;
        foreach (var q in query)
        {
            if (char.IsWhiteSpace(q)) { run = 0; continue; }
            var found = false;
            for (; t < text.Length; t++)
            {
                if (char.ToLowerInvariant(text[t]) != char.ToLowerInvariant(q)) { run = 0; continue; }
                var wordStart = t == 0 || !char.IsLetterOrDigit(text[t - 1]) || char.IsUpper(text[t]) && char.IsLower(text[t - 1]);
                run++;
                score += 1 + run * 2 + (wordStart ? 6 : 0) + Math.Max(0, 10 - t / 4);
                t++;
                found = true;
                break;
            }
            if (!found) return 0;
        }
        return score;
    }
}
