using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

namespace AzureWorkbench.Views;

/// <summary>View and edit an app's application settings (environment variables) and connection strings.</summary>
public sealed class AppConfigView : UserControl, IWorkspaceView
{
    const char Mask = '•';
    readonly AppConfigService _service;
    readonly WebAppRef _app;
    readonly CancellationTokenSource _lifetime = new();
    readonly TextBlock _status = Ui.StatusLine();
    readonly TextBlock _summary = new() { Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressBar _progress = Ui.Progress();
    readonly TextBox _filter = new() { Watermark = "Filter by name or value", Width = 220 };
    readonly CheckBox _showValues = new() { Content = "Show values" };
    readonly ToggleButton _advanced = new() { Content = "Advanced edit (JSON)" };
    readonly TabControl _tabs;
    readonly ComparePanel _compare;
    TabItem? _compareTab;
    readonly Section _settings;
    readonly Section _connections;
    readonly Button _save;
    readonly Button _discard;
    readonly Button _reload;
    readonly Button _compareButton;
    readonly Button _exportButton;
    readonly DockPanel _work = new();
    AppConfiguration? _loaded;
    bool _closed;

    public bool IsBusy { get; private set; }
    public string? UnsavedWork => HasChanges ? "unsaved configuration changes" : null;
    public bool HasChanges => _loaded != null
        && (_settings.JsonEdited || _connections.JsonEdited || Diff.Between(_loaded, Current(strict: false)).Count > 0);
    public WebAppRef App => _app;

    readonly IWorkbench? _workbench;

    /// <summary>The configuration last loaded from (or saved to) Azure; null until loaded.</summary>
    public AppConfiguration? LoadedConfiguration => _loaded;

    public AppConfigView(WebAppRef app, AppConfigService service, IWorkbench? workbench = null)
    {
        _workbench = workbench;
        _app = app;
        _service = service;
        _settings = new Section(this, connectionStrings: false);
        _connections = new Section(this, connectionStrings: true);
        _reload = Ui.Button("Reload", ConfirmReload);
        _save = Ui.Primary("Save changes...", ConfirmSave);
        _discard = Ui.Button("Discard", () =>
        {
            if (_loaded == null) return;
            _revertingAdvanced = true;
            _advanced.IsChecked = false;
            _revertingAdvanced = false;
            _settings.LeaveAdvanced();
            _connections.LeaveAdvanced();
            _filter.IsEnabled = true;
            Show(_loaded);
            _status.Text = "Edits discarded.";
        });
        _filter.TextChanged += (_, _) => { _settings.ApplyFilter(); _connections.ApplyFilter(); };
        _compare = new ComparePanel(this);
        _showValues.IsCheckedChanged += (_, _) => { _settings.ApplyMask(); _connections.ApplyMask(); _compare.Refresh(); };
        _advanced.IsCheckedChanged += (_, _) => ToggleAdvanced();

        _tabs = Ui.Tabs(("Application settings", _settings.Root), ("Connection strings", _connections.Root));
        _compareButton = Ui.Button("Compare with...", ChooseCompare);
        _exportButton = Ui.Button("Export .env", ConfirmExport);
        var header = Ui.Header(
            $"{(app.IsFunction ? "Function App" : "App Service")} / {app.Name} · configuration", app.Id,
            "Application settings are exposed to the app as environment variables. Saving settings or connection strings restarts the app. " +
            "Slot settings stay with this slot during swaps. Values are hidden until you choose Show values.");
        _work.Children.Add(Ui.Dock(header, Avalonia.Controls.Dock.Top));
        _work.Children.Add(Ui.Dock(Ui.Toolbar(_save, _discard, _reload), Avalonia.Controls.Dock.Top));
        _work.Children.Add(Ui.Dock(Ui.Toolbar(
            Ui.Button("Add", () => Current_Section().Add()), _filter, _showValues, _advanced, _compareButton, _exportButton, _summary),
            Avalonia.Controls.Dock.Top));
        _work.Children.Add(_tabs);
        Content = new DockPanel
        {
            Background = VsCode.Editor,
            Children = { Ui.Dock(_progress, Avalonia.Controls.Dock.Top), Ui.Dock(_status, Avalonia.Controls.Dock.Bottom), _work },
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        UpdateState();
    }

    public Task LoadAsync() => RunAsync("Loading configuration...", async ct =>
    {
        var config = await _service.GetAsync(_app, ct);
        Show(config);
        return $"Loaded {config.Settings.Count} settings and {config.ConnectionStrings.Count} connection strings at {DateTime.Now:HH:mm:ss}.";
    });

    public Task CloseAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the configuration save to finish before closing this tab.");
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        return Task.CompletedTask;
    }

    Section Current_Section()
    {
        if (_tabs.SelectedIndex == 2) _tabs.SelectedIndex = 0;
        return _tabs.SelectedIndex == 1 ? _connections : _settings;
    }

    void Show(AppConfiguration config)
    {
        _loaded = config;
        _settings.Load(config.Settings.Select(s => new Row(s.Name, s.Value, null, s.SlotSetting)));
        _connections.Load(config.ConnectionStrings.Select(c => new Row(c.Name, c.Value, c.Type, c.SlotSetting)));
        UpdateState();
    }

    /// <summary>The configuration as currently edited. With strict, unapplied advanced-edit JSON is parsed (and may throw).</summary>
    AppConfiguration Current(bool strict)
    {
        if (strict && _advanced.IsChecked == true) ApplyJson(leaveAdvanced: false);
        return new AppConfiguration(
            _settings.Rows.Select(r => new AppSetting(r.Name, r.Value, r.Slot)).ToList(),
            _connections.Rows.Select(r => new ConnectionStringSetting(r.Name, r.Value, r.Type ?? "Custom", r.Slot)).ToList());
    }

    void ConfirmReload()
    {
        if (!HasChanges)
        {
            _ = LoadAsync();
            return;
        }
        Ui.Confirm(_reload, "Reloading discards your unsaved edits.", "Discard edits and reload", () => _ = LoadAsync(), destructive: true);
    }

    void ConfirmSave()
    {
        if (_loaded is not { } loaded || IsBusy) return;
        AppConfiguration updated;
        try { updated = Current(strict: true); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            _status.Text = $"Advanced edit JSON is invalid: {ex.Message}";
            return;
        }
        var changes = Diff.Between(loaded, updated);
        if (changes.Count == 0)
        {
            _status.Text = "No changes to save.";
            return;
        }
        var (errors, warnings) = updated.Validate(_app.IsLinux);
        if (errors.Count > 0)
        {
            _status.Text = "Fix before saving: " + string.Join(" ", errors);
            return;
        }
        var restarts = changes.Any(c => c.Kind != ChangeKind.Changed || c.Detail != "slot setting");
        var lines = changes.Take(25).Select(c => c.ToString()).ToList();
        if (changes.Count > 25) lines.Add($"…and {changes.Count - 25} more");
        lines.AddRange(warnings.Select(w => "⚠ " + w));
        Ui.Confirm(_save,
            $"Save {changes.Count} change{(changes.Count == 1 ? "" : "s")} to {_app.Name}?" +
            (restarts ? " The app will restart." : "") + "\n\n" + string.Join("\n", lines),
            restarts ? "Save and restart app" : "Save",
            () => _ = SaveAsync(loaded, updated));
    }

    Task SaveAsync(AppConfiguration loaded, AppConfiguration updated) => RunAsync("Saving configuration...", async ct =>
    {
        try
        {
            var saved = await _service.SaveAsync(_app, loaded, updated, ct);
            Show(saved);
            return $"Saved at {DateTime.Now:HH:mm:ss}. The app restarts to apply application setting and connection string changes.";
        }
        catch (ConfigConflictException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            throw new InvalidOperationException($"Save may be incomplete; your edits were kept. Reload to check what Azure holds. {ex.Message}", ex);
        }
    });

    async Task RunAsync(string activity, Func<CancellationToken, Task<string>> operation)
    {
        if (IsBusy || _closed) return;
        IsBusy = true;
        _status.Text = activity;
        UpdateState();
        try { _status.Text = await operation(_lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _status.Text = $"Configuration error: {ex.Message}"; }
        finally
        {
            IsBusy = false;
            if (!_closed) UpdateState();
        }
    }

    bool _revertingAdvanced;

    void ToggleAdvanced()
    {
        if (_revertingAdvanced) return;
        var advanced = _advanced.IsChecked == true;
        if (advanced)
        {
            _settings.EnterAdvanced();
            _connections.EnterAdvanced();
        }
        else
        {
            try { ApplyJson(leaveAdvanced: true); }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                _revertingAdvanced = true;
                _advanced.IsChecked = true;
                _revertingAdvanced = false;
                _status.Text = $"Advanced edit JSON is invalid, fix it or discard changes: {ex.Message}";
                return;
            }
        }
        _filter.IsEnabled = !advanced;
        UpdateState();
    }

    /// <summary>Parses both JSON editors before changing either, so invalid JSON never half-applies.</summary>
    void ApplyJson(bool leaveAdvanced)
    {
        var settings = _settings.ParseJson();
        var connections = _connections.ParseJson();
        _settings.ApplyRows(settings, leaveAdvanced);
        _connections.ApplyRows(connections, leaveAdvanced);
    }

    // ---- Export and compare ----

    void ConfirmExport()
    {
        if (_loaded == null || IsBusy) return;
        AppConfiguration current;
        try { current = Current(strict: true); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            _status.Text = $"Advanced edit JSON is invalid: {ex.Message}";
            return;
        }
        Ui.Confirm(_exportButton,
            $"Copy {current.Settings.Count} application setting{(current.Settings.Count == 1 ? "" : "s")} as KEY=value lines to the clipboard? " +
            "The values are secrets in plain text, so paste them only somewhere safe.",
            "Copy to clipboard", async () =>
            {
                var ok = await Ui.CopyAsync(this, ConfigComparison.ToEnv(current.Settings));
                _status.Text = ok ? $"Copied {current.Settings.Count} setting{(current.Settings.Count == 1 ? "" : "s")} as .env to the clipboard."
                    : "No clipboard is available.";
            });
    }

    static string Describe(AppConfigView v) => $"{(v.App.IsFunction ? "Function App" : "App Service")} · {v.App.Name}";

    void ChooseCompare()
    {
        if (_loaded == null) return;
        var candidates = (_workbench?.OpenConfigurations ?? []).Where(v => v != this && v.LoadedConfiguration != null).ToList();
        if (candidates.Count == 0)
        {
            _status.Text = "Open another app's Configuration tab first.";
            return;
        }
        Ui.Pick(_compareButton, "Compare this app with", candidates, Describe, StartCompare);
    }

    void StartCompare(AppConfigView other)
    {
        if (other.LoadedConfiguration is not { } config) return;
        _compare.SetOther(Describe(other), config);
        if (_compareTab == null)
        {
            _compareTab = new TabItem { Header = "COMPARE", Content = _compare.Root };
            _tabs.Items.Add(_compareTab);
        }
        _tabs.SelectedItem = _compareTab;
        _status.Text = $"Comparing with {Describe(other)}.";
    }

    /// <summary>Copies a compared row into this view's editable rows. Nothing is saved.</summary>
    void CopyRow(ConfigCompareRow row)
    {
        if (row.Section == ConfigComparison.ConnectionSection)
            _connections.Upsert(row.Name, row.RightValue ?? "", row.RightType ?? "Custom", row.RightSlot);
        else
            _settings.Upsert(row.Name, row.RightValue ?? "", null, row.RightSlot);
    }

    internal void UpdateState()
    {
        if (_closed) return;
        _progress.IsVisible = IsBusy;
        _work.IsEnabled = !IsBusy;
        var loaded = _loaded != null;
        _tabs.IsEnabled = loaded;
        var changes = loaded ? Diff.Between(_loaded!, Current(strict: false)).Count : 0;
        var jsonEdited = _settings.JsonEdited || _connections.JsonEdited;
        _save.IsEnabled = loaded && (changes > 0 || jsonEdited);
        _discard.IsEnabled = loaded && (changes > 0 || jsonEdited || _advanced.IsChecked == true);
        _compare.Refresh();
        _summary.Text = !loaded ? "" : changes == 0 ? "No unsaved changes" : $"{changes} unsaved change{(changes == 1 ? "" : "s")}";
        _settings.UpdateMarkers(_loaded?.Settings.ToDictionary(s => s.Name, s => new Row(s.Name, s.Value, null, s.SlotSetting), StringComparer.OrdinalIgnoreCase));
        _connections.UpdateMarkers(_loaded?.ConnectionStrings.ToDictionary(c => c.Name, c => new Row(c.Name, c.Value, c.Type, c.SlotSetting), StringComparer.OrdinalIgnoreCase));
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Ui.IsCommand(e.KeyModifiers))
        {
            ConfirmSave();
            e.Handled = true;
        }
    }

    internal string FilterText => _filter.Text ?? "";
    internal bool ValuesVisible => _showValues.IsChecked == true;

    sealed record Row(string Name, string Value, string? Type, bool Slot);

    /// <summary>Copies the given compared rows into this app's editable rows (unsaved). Returns the number copied.</summary>
    int CopySelected(IReadOnlyList<ConfigCompareRow> rows)
    {
        if (_advanced.IsChecked == true)
        {
            _status.Text = "Leave Advanced edit before copying rows.";
            return 0;
        }
        foreach (var row in rows) CopyRow(row);
        UpdateState();
        _status.Text = rows.Count == 0 ? "Nothing selected to copy."
            : $"Copied {rows.Count} row{(rows.Count == 1 ? "" : "s")} into this app. Review them, then choose Save changes... to apply.";
        return rows.Count;
    }

    /// <summary>The compare grid: this app's current edits against another app's saved configuration.</summary>
    sealed class ComparePanel
    {
        readonly AppConfigView _owner;
        readonly StackPanel _rows = new() { Spacing = 2, Margin = new Thickness(12, 4) };
        readonly CheckBox _hideSame = new() { Content = "Hide identical", IsChecked = true };
        readonly TextBlock _info = new() { Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _empty = new() { Opacity = Ui.Muted, Margin = new Thickness(16, 8) };
        readonly Button _copy;
        readonly HashSet<(string, string)> _selected = [];
        readonly TextBlock _otherHeader = new() { Text = "Other app" };
        AppConfiguration? _other;
        List<ConfigCompareRow> _copyable = [];
        const string Cols = "30,34,120,2*,2*,2*";

        public Control Root { get; }

        public ComparePanel(AppConfigView owner)
        {
            _owner = owner;
            _copy = Ui.Primary("Copy selected into this app", () =>
            {
                var rows = _copyable.Where(r => _selected.Contains((r.Section, r.Name.ToLowerInvariant()))).ToList();
                if (_owner.CopySelected(rows) > 0) _selected.Clear();
                Refresh();
            });
            _copy.IsEnabled = false;
            _hideSame.IsCheckedChanged += (_, _) => Refresh();
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(15, 6, 12, 2), Opacity = Ui.Muted };
            columns.Children.Add(Ui.At(new TextBlock { Text = "Section" }, col: 2));
            columns.Children.Add(Ui.At(new TextBlock { Text = "Name" }, col: 3));
            columns.Children.Add(Ui.At(new TextBlock { Text = "This app" }, col: 4));
            columns.Children.Add(Ui.At(_otherHeader, col: 5));
            var root = new DockPanel();
            root.Children.Add(Ui.Dock(Ui.Hint("Compares this tab's current edits with the other app's saved configuration (not its unsaved edits). " +
                "Copying changes only this tab's rows; use Save changes... to review and apply."), Avalonia.Controls.Dock.Top));
            root.Children.Add(Ui.Dock(Ui.Toolbar(_hideSame, Ui.Button("Select all differences", SelectAll), _copy, _info), Avalonia.Controls.Dock.Top));
            root.Children.Add(Ui.Dock(columns, Avalonia.Controls.Dock.Top));
            root.Children.Add(Ui.Dock(_empty, Avalonia.Controls.Dock.Top));
            root.Children.Add(new ScrollViewer { Content = _rows });
            Root = root;
        }

        public void SetOther(string label, AppConfiguration other)
        {
            _other = other;
            _otherHeader.Text = label;
            _selected.Clear();
            Refresh();
        }

        void SelectAll()
        {
            foreach (var row in _copyable) _selected.Add((row.Section, row.Name.ToLowerInvariant()));
            Refresh();
        }

        string Show(string? value, bool slot, string? type)
        {
            if (value == null) return "—";
            var text = value.Length == 0 ? "(empty)" : _owner.ValuesVisible ? value : new string(Mask, Math.Min(value.Length, 12));
            return text + (type != null ? $"  · {type}" : "") + (slot ? "  · slot" : "");
        }

        public void Refresh()
        {
            if (_other == null) return;
            var all = ConfigComparison.Compare(_owner.Current(strict: false), _other);
            _copyable = all.Where(r => r.State is CompareState.OnlyRight or CompareState.Different).ToList();
            _selected.RemoveWhere(k => !_copyable.Any(r => (r.Section, r.Name.ToLowerInvariant()) == k));
            var shown = all.Where(r => _hideSame.IsChecked != true || r.State != CompareState.Same).ToList();
            _rows.Children.Clear();
            foreach (var row in shown) _rows.Children.Add(BuildRow(row));
            _empty.IsVisible = shown.Count == 0;
            _empty.Text = all.Count == 0 ? "Both apps have no settings or connection strings." : "No differences: everything matches.";
            _info.Text = $"{all.Count(r => r.State == CompareState.Different)} different, {all.Count(r => r.State == CompareState.OnlyRight)} only in other app, " +
                         $"{all.Count(r => r.State == CompareState.OnlyLeft)} only in this app, {all.Count(r => r.State == CompareState.Same)} identical";
            _copy.IsEnabled = _selected.Count > 0;
        }

        Control BuildRow(ConfigCompareRow row)
        {
            var (glyph, color, tip) = row.State switch
            {
                CompareState.Same => ("=", VsCode.Glyphs.Tenant, "Identical"),
                CompareState.Different => ("≠", VsCode.Glyphs.Subscription, "Different"),
                CompareState.OnlyRight => ("←", VsCode.Glyphs.Deployments, "Only in the other app"),
                _ => ("→", VsCode.Glyphs.ServiceBus, "Only in this app"),
            };
            var key = (row.Section, row.Name.ToLowerInvariant());
            var copyable = row.State is CompareState.OnlyRight or CompareState.Different;
            var check = new CheckBox { IsChecked = _selected.Contains(key), IsEnabled = copyable, HorizontalAlignment = HorizontalAlignment.Center };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) _selected.Add(key); else _selected.Remove(key);
                _copy.IsEnabled = _selected.Count > 0;
            };
            var badge = Ui.Glyph(glyph, color);
            ToolTip.SetTip(badge, tip);
            var mono = VsCode.Mono;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(4, 1, 0, 1) };
            grid.Children.Add(Ui.At(badge, col: 0));
            grid.Children.Add(Ui.At(check, col: 1));
            grid.Children.Add(Ui.At(new TextBlock { Text = row.Section, Opacity = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }, col: 2));
            grid.Children.Add(Ui.At(new SelectableTextBlock { Text = row.Name, FontFamily = mono, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, col: 3));
            grid.Children.Add(Ui.At(new SelectableTextBlock { Text = Show(row.LeftValue, row.LeftSlot, row.LeftType), FontFamily = mono, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, col: 4));
            grid.Children.Add(Ui.At(new SelectableTextBlock { Text = Show(row.RightValue, row.RightSlot, row.RightType), FontFamily = mono, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, col: 5));
            return grid;
        }
    }

    /// <summary>One tab: a list of editable rows plus an advanced JSON editor.</summary>
    sealed class Section
    {
        readonly AppConfigView _owner;
        readonly bool _connectionStrings;
        readonly StackPanel _rows = new() { Spacing = 2, Margin = new Thickness(12, 4) };
        readonly TextBox _json = new()
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = VsCode.Mono, FontSize = 13,
            Margin = new Thickness(12, 4),
        };
        readonly TextBlock _empty = new() { Opacity = Ui.Muted, Margin = new Thickness(12, 8) };
        readonly DockPanel _list = new();
        readonly DockPanel _editor = new() { IsVisible = false };
        readonly List<RowEditor> _editors = [];

        public Grid Root { get; } = new();
        public IEnumerable<(string Name, string Value, string? Type, bool Slot)> Rows =>
            _editors.Select(e => (e.Name.Text?.Trim() ?? "", e.Value.Text ?? "", e.Type?.SelectedItem as string, e.Slot.IsChecked == true));

        public Section(AppConfigView owner, bool connectionStrings)
        {
            _owner = owner;
            _connectionStrings = connectionStrings;
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions(RowEditor.Columns(connectionStrings)), Margin = new Thickness(15, 6, 12, 2), Opacity = Ui.Muted };
            var labels = connectionStrings ? new[] { "Name", "Value", "Type", "Slot", "" } : ["Name", "Value", "Slot", ""];
            for (var i = 0; i < labels.Length; i++) columns.Children.Add(Ui.At(new TextBlock { Text = labels[i] }, col: i));
            _list.Children.Add(Ui.Dock(columns, Avalonia.Controls.Dock.Top));
            _list.Children.Add(Ui.Dock(_empty, Avalonia.Controls.Dock.Top));
            _list.Children.Add(new ScrollViewer { Content = _rows });
            _editor.Children.Add(Ui.Dock(new TextBlock
            {
                Text = connectionStrings
                    ? "Portal-compatible JSON: [{ \"name\": \"Db\", \"value\": \"...\", \"type\": \"SQLAzure\", \"slotSetting\": false }]. Click Save changes to review, or switch back to apply."
                    : "Portal-compatible JSON: [{ \"name\": \"KEY\", \"value\": \"...\", \"slotSetting\": false }]. Click Save changes to review, or switch back to apply.",
                Opacity = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6),
            }, Avalonia.Controls.Dock.Top));
            _editor.Children.Add(_json);
            _json.TextChanged += (_, _) => _owner.UpdateState();
            Root.Children.Add(_list);
            Root.Children.Add(_editor);
        }

        public void Load(IEnumerable<Row> rows)
        {
            _rows.Children.Clear();
            _editors.Clear();
            foreach (var row in rows) Append(row);
            if (_editor.IsVisible) _json.Text = _jsonBaseline = ToJson();
            ApplyFilter();
        }

        public void Add()
        {
            if (_editor.IsVisible) return;
            var editor = Append(new Row("", "", _connectionStrings ? "Custom" : null, false));
            ApplyFilter();
            _owner.UpdateState();
            editor.Name.Focus();
            editor.Root.BringIntoView();
        }

        /// <summary>Overwrites the row with this name (case-insensitive, keeping this app's casing) or appends a new one.</summary>
        public void Upsert(string name, string value, string? type, bool slot)
        {
            var editor = _editors.FirstOrDefault(e => string.Equals(e.Name.Text?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (editor == null)
            {
                Append(new Row(name, value, _connectionStrings ? type ?? "Custom" : null, slot));
                ApplyFilter();
                return;
            }
            editor.Value.Text = value;
            editor.Slot.IsChecked = slot;
            if (editor.Type != null && type != null)
            {
                if (!editor.Type.Items.Contains(type)) editor.Type.ItemsSource = AppConfiguration.ConnectionStringTypes.Append(type).Distinct().ToList();
                editor.Type.SelectedItem = type;
            }
        }

        RowEditor Append(Row row)
        {
            var editor = new RowEditor(row, _connectionStrings, _owner.ValuesVisible ? '\0' : Mask);
            editor.Changed += _owner.UpdateState;
            editor.Removed += () =>
            {
                _editors.Remove(editor);
                _rows.Children.Remove(editor.Root);
                ApplyFilter();
                _owner.UpdateState();
            };
            _editors.Add(editor);
            _rows.Children.Add(editor.Root);
            return editor;
        }

        public void ApplyFilter()
        {
            var filter = _owner.FilterText.Trim();
            var visible = 0;
            foreach (var editor in _editors)
            {
                editor.Root.IsVisible = filter.Length == 0 || string.IsNullOrEmpty(editor.Name.Text)
                    || editor.Name.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (_owner.ValuesVisible && (editor.Value.Text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase));
                if (editor.Root.IsVisible) visible++;
            }
            _empty.IsVisible = visible == 0;
            _empty.Text = _editors.Count == 0 ? $"No {(_connectionStrings ? "connection strings" : "application settings")}. Click Add to create one." : "Nothing matches the filter.";
        }

        public void ApplyMask()
        {
            foreach (var editor in _editors) editor.Value.PasswordChar = _owner.ValuesVisible ? '\0' : Mask;
            ApplyFilter();
        }

        string? _jsonBaseline;

        public bool JsonEdited => _editor.IsVisible && _json.Text != _jsonBaseline;

        public void EnterAdvanced()
        {
            _json.Text = _jsonBaseline = ToJson();
            _list.IsVisible = false;
            _editor.IsVisible = true;
        }

        public void LeaveAdvanced()
        {
            _editor.IsVisible = false;
            _list.IsVisible = true;
        }

        public List<Row>? ParseJson()
        {
            if (!_editor.IsVisible) return null;
            var json = string.IsNullOrWhiteSpace(_json.Text) ? "[]" : _json.Text;
            return _connectionStrings
                ? AppConfiguration.ConnectionStringsFromJson(json).Select(c => new Row(c.Name, c.Value, c.Type, c.SlotSetting)).ToList()
                : AppConfiguration.SettingsFromJson(json).Select(s => new Row(s.Name, s.Value, null, s.SlotSetting)).ToList();
        }

        public void ApplyRows(List<Row>? rows, bool leaveAdvanced)
        {
            if (rows == null) return;
            _rows.Children.Clear();
            _editors.Clear();
            foreach (var row in rows) Append(row);
            if (leaveAdvanced) LeaveAdvanced();
            else _json.Text = _jsonBaseline = ToJson();
            ApplyFilter();
        }

        string ToJson() => _connectionStrings
            ? AppConfiguration.ConnectionStringsToJson(Rows.Select(r => new ConnectionStringSetting(r.Name, r.Value, r.Type ?? "Custom", r.Slot)))
            : AppConfiguration.SettingsToJson(Rows.Select(r => new AppSetting(r.Name, r.Value, r.Slot)));

        public void UpdateMarkers(Dictionary<string, Row>? original)
        {
            foreach (var editor in _editors)
            {
                var name = editor.Name.Text?.Trim() ?? "";
                var state = original == null ? 0
                    : !original.TryGetValue(name, out var o) ? 1
                    : o.Name != name || o.Value != (editor.Value.Text ?? "") || o.Slot != (editor.Slot.IsChecked == true)
                      || (_connectionStrings && o.Type != editor.Type?.SelectedItem as string) ? 2 : 0;
                editor.Root.BorderBrush = state switch { 1 => Added, 2 => Modified, _ => Brushes.Transparent };
                ToolTip.SetTip(editor.Root, state switch { 1 => "New", 2 => "Modified", _ => null });
            }
        }

        static readonly IBrush Added = new SolidColorBrush(Color.Parse("#487e02"));
        static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#1b81a8"));
    }

    sealed class RowEditor
    {
        public static string Columns(bool connectionStrings) => connectionStrings ? "2*,3*,130,50,32" : "2*,3*,50,32";

        public Border Root { get; }
        public TextBox Name { get; }
        public TextBox Value { get; }
        public ComboBox? Type { get; }
        public CheckBox Slot { get; }
        public event Action? Changed;
        public event Action? Removed;

        public RowEditor(Row row, bool connectionStrings, char mask)
        {
            Name = new TextBox { Text = row.Name, Watermark = "NAME", FontFamily = VsCode.Mono, Margin = new Thickness(0, 0, 4, 0) };
            Value = new TextBox { Text = row.Value, Watermark = "value", FontFamily = VsCode.Mono, PasswordChar = mask, Margin = new Thickness(0, 0, 4, 0) };
            Slot = new CheckBox { IsChecked = row.Slot, HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(Slot, "Deployment slot setting: stays with this slot during swaps.");
            var remove = new Button { Content = "×", Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(remove, "Remove");
            remove.Click += (_, _) => Removed?.Invoke();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns(connectionStrings)) };
            grid.Children.Add(Ui.At(Name, col: 0));
            grid.Children.Add(Ui.At(Value, col: 1));
            var col = 2;
            if (connectionStrings)
            {
                var types = AppConfiguration.ConnectionStringTypes.ToList();
                if (row.Type != null && !types.Contains(row.Type)) types.Add(row.Type);
                Type = new ComboBox { ItemsSource = types, SelectedItem = row.Type ?? "Custom", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 4, 0) };
                Type.SelectionChanged += (_, _) => Changed?.Invoke();
                grid.Children.Add(Ui.At(Type, col: col++));
            }
            grid.Children.Add(Ui.At(Slot, col: col++));
            grid.Children.Add(Ui.At(remove, col: col));
            Name.TextChanged += (_, _) => Changed?.Invoke();
            Value.TextChanged += (_, _) => Changed?.Invoke();
            Slot.IsCheckedChanged += (_, _) => Changed?.Invoke();
            Root = new Border { Child = grid, BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Transparent, Padding = new Thickness(4, 1, 0, 1) };
        }
    }
}
