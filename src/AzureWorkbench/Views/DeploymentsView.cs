using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

namespace AzureWorkbench.Views;

/// <summary>Read-only deployment history for an App Service / Function App, with the log of the selected deployment.</summary>
public sealed class DeploymentsView : UserControl, IWorkspaceView
{
    const string Columns = "120,140,80,140,110,*,70";
    readonly WebAppRef _app;
    readonly DeploymentService _service;
    readonly IWorkbench? _workbench;
    readonly CancellationTokenSource _lifetime = new();
    readonly ProgressBar _progress = Ui.Progress();
    readonly TextBlock _status = Ui.StatusLine();
    readonly TextBlock _summary = new() { Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _empty = Ui.Hint("");
    readonly ListBox _list = new() { Background = Brushes.Transparent, SelectionMode = SelectionMode.Single };
    readonly SelectableTextBlock _output = Ui.Output();
    readonly Button _copy;
    CancellationTokenSource? _selection;
    Deployment? _shown;
    string _detailsText = "";
    bool _closed;

    public bool IsBusy { get; private set; }
    public string? UnsavedWork => null;
    public WebAppRef App => _app;

    public DeploymentsView(WebAppRef app, DeploymentService service, IWorkbench? workbench = null)
    {
        _app = app;
        _service = service;
        _workbench = workbench;
        _copy = Ui.Button("Copy details", () => _ = CopyAsync());
        _copy.IsEnabled = false;
        _empty.IsVisible = false;
        _empty.Margin = new Thickness(16, 8);
        _list.SelectionChanged += (_, _) => _ = ShowSelectedAsync();

        var header = Ui.Header($"{(app.IsFunction ? "Function App" : "App Service")} / {app.Name} · deployments", app.Id,
            "Deployment history recorded by App Service (Kudu). Select a deployment to see its log. This view is read-only.");
        var toolbar = Ui.Toolbar(Ui.Primary("Refresh", () => _ = LoadAsync()), _copy, _summary);
        var columns = Row("Status", "Started", "Duration", "Author", "Deployer", "Message", "");
        columns.Margin = new Thickness(16, 6);
        columns.Opacity = Ui.Muted;

        var panels = new Grid { RowDefinitions = new RowDefinitions("*,5,*") };
        panels.RowDefinitions[0].MinHeight = panels.RowDefinitions[2].MinHeight = 80;
        panels.Children.Add(new DockPanel
        {
            Children = { Ui.Dock(columns, Avalonia.Controls.Dock.Top), Ui.Dock(_empty, Avalonia.Controls.Dock.Top), _list },
        });
        panels.Children.Add(Ui.At(new GridSplitter(), row: 1));
        panels.Children.Add(Ui.At(new DockPanel
        {
            Children = { Ui.Dock(Ui.Section("Details"), Avalonia.Controls.Dock.Top), new ScrollViewer { Content = _output } },
        }, row: 2));

        Content = new DockPanel
        {
            Background = VsCode.Editor,
            Children =
            {
                Ui.Dock(_progress, Avalonia.Controls.Dock.Top),
                Ui.Dock(header, Avalonia.Controls.Dock.Top),
                Ui.Dock(toolbar, Avalonia.Controls.Dock.Top),
                Ui.Dock(_status, Avalonia.Controls.Dock.Bottom),
                panels,
            },
        };
    }

    public async Task LoadAsync()
    {
        if (IsBusy || _closed) return;
        IsBusy = true;
        _progress.IsVisible = true;
        _status.Text = "Loading deployments...";
        try
        {
            var deployments = await _service.ListAsync(_app, _lifetime.Token);
            if (_closed) return;
            Populate(deployments);
            _status.Text = $"Loaded {deployments.Count} deployment{(deployments.Count == 1 ? "" : "s")} at {DateTime.Now:HH:mm:ss}.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _status.Text = $"Deployment error: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            if (!_closed) _progress.IsVisible = false;
        }
    }

    /// <summary>Cancels any in-flight request. Reading is cancellable, so this never throws.</summary>
    public Task CloseAsync()
    {
        _closed = true;
        _selection?.Cancel();
        _lifetime.Cancel();
        return Task.CompletedTask;
    }

    void Populate(List<Deployment> deployments)
    {
        var previous = (_list.SelectedItem as ListBoxItem)?.Tag as Deployment;
        _list.ItemsSource = deployments.Select(d => new ListBoxItem { Content = RowFor(d), Tag = d }).ToList();
        _empty.IsVisible = deployments.Count == 0;
        _empty.Text = "No deployments recorded. Apps deployed by zip/run-from-package, container images or some CI pipelines " +
            "(and Flex Consumption apps) may not record history here.";
        var lastSuccess = deployments.Where(d => d.IsSuccess && (d.End ?? d.Start) != null).Select(d => d.End ?? d.Start).Max();
        _summary.Text = $"{deployments.Count} deployment{(deployments.Count == 1 ? "" : "s")}"
            + (lastSuccess is { } t ? $" · last success {Relative(t)}" : deployments.Count > 0 ? " · no successful deployment" : "");
        _shown = null;
        if (previous != null && _list.ItemsSource is List<ListBoxItem> items && items.FirstOrDefault(i => ((Deployment)i.Tag!).Id == previous.Id) is { } same)
            _list.SelectedItem = same;
        else
        {
            _output.Text = "";
            _detailsText = "";
            _copy.IsEnabled = false;
        }
    }

    Control RowFor(Deployment d)
    {
        var brush = d.IsSuccess ? VsCode.Success : d.IsFailed ? VsCode.Danger : d.InProgress ? VsCode.Warning : VsCode.SectionHeader;
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(new TextBlock { Text = d.StatusText, Foreground = brush });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(4, 2) };
        grid.Children.Add(Ui.At(status, col: 0));
        grid.Children.Add(Ui.At(Cell(Started(d)), col: 1));
        grid.Children.Add(Ui.At(Cell(Deployment.FormatDuration(d.Duration)), col: 2));
        grid.Children.Add(Ui.At(Cell(d.Author), col: 3));
        grid.Children.Add(Ui.At(Cell(d.Deployer), col: 4));
        grid.Children.Add(Ui.At(Cell(FirstLine(d.Message)), col: 5));
        if (d.Active)
            grid.Children.Add(Ui.At(new TextBlock
            {
                Text = "ACTIVE", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = VsCode.Success,
                VerticalAlignment = VerticalAlignment.Center,
            }, col: 6));
        return grid;
    }

    static TextBlock Cell(string text) => new()
    {
        Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
    };

    static Grid Row(params string[] headers)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        for (var i = 0; i < headers.Length; i++) grid.Children.Add(Ui.At(new TextBlock { Text = headers[i] }, col: i));
        return grid;
    }

    const int MaxDetails = 20;

    async Task ShowSelectedAsync()
    {
        _selection?.Cancel();
        if ((_list.SelectedItem as ListBoxItem)?.Tag is not Deployment d || _closed)
        {
            _copy.IsEnabled = false;
            return;
        }
        var selection = _selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _shown = d;
        _detailsText = Summary(d);
        _output.Text = _detailsText + "\n\nLoading log...";
        _copy.IsEnabled = true;
        _progress.IsVisible = true;
        try
        {
            var log = await _service.GetLogAsync(_app, d.Id, selection.Token);
            _detailsText = Summary(d) + "\n\n" + (log.Count == 0 ? "No log entries recorded." : string.Join("\n", log.Select(Line)));
            var nested = log.Where(e => !string.IsNullOrEmpty(e.DetailsId)).Select(e => e.DetailsId!).Distinct().Take(MaxDetails).ToList();
            _output.Text = _detailsText + (nested.Count > 0 ? "\n\nLoading detailed build output..." : "");
            if (nested.Count > 0)
            {
                // Kudu keeps build/deploy output (e.g. Oryx) in nested logs; show each under its step, indented.
                var details = await _service.GetLogDetailsAsync(_app, d.Id, nested, selection.Token);
                selection.Token.ThrowIfCancellationRequested();
                var text = new StringBuilder(Summary(d)).Append("\n\n");
                foreach (var entry in log)
                {
                    text.AppendLine(Line(entry));
                    if (entry.DetailsId is { } id && details.TryGetValue(id, out var body) && body.Length > 0)
                        foreach (var line in body.Split('\n')) text.Append("            ").AppendLine(line.TrimEnd('\r'));
                }
                if (log.Count(e => !string.IsNullOrEmpty(e.DetailsId)) > MaxDetails)
                    text.AppendLine($"\n[Only the first {MaxDetails} detailed logs are shown.]");
                if (details.Count == 0) text.AppendLine("\n[Detailed output is only available from the app's public Kudu (SCM) endpoint.]");
                _detailsText = text.ToString().TrimEnd();
            }
            _output.Text = _detailsText;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (selection.IsCancellationRequested) return;
            _output.Text = _detailsText + $"\n\nCould not load log: {ex.Message}";
            _status.Text = $"Deployment log error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_selection, selection) && !_closed) _progress.IsVisible = IsBusy;
        }
    }

    async Task CopyAsync()
    {
        if (_shown == null) return;
        var ok = await Ui.CopyAsync(this, _detailsText);
        _status.Text = ok ? "Deployment details copied." : "No clipboard is available.";
        if (ok) _workbench?.Notify("Deployment details copied");
    }

    static string Summary(Deployment d)
    {
        var text = new StringBuilder();
        text.AppendLine($"Deployment: {d.Id}{(d.Active ? " (active)" : "")}");
        text.AppendLine($"Status:     {d.StatusText}");
        text.AppendLine($"Started:    {Started(d)}");
        text.AppendLine($"Ended:      {(d.End is { } e ? e.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "")}");
        text.AppendLine($"Duration:   {Deployment.FormatDuration(d.Duration)}");
        text.AppendLine($"Author:     {d.Author}");
        text.AppendLine($"Deployer:   {d.Deployer}");
        text.Append($"Message:    {d.Message}");
        return text.ToString();
    }

    static string Line(DeploymentLogEntry e) =>
        $"{(e.Time is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "--:--:--")}  {e.Message}";

    static string Started(Deployment d) => d.Start is { } s ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.UtcNow - time;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
