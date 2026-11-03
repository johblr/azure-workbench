using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Avalonia;
using Dock.Model.Avalonia.Controls;

namespace AzureWorkbench.Views;

/// <summary>Implemented by tab views that can be busy or hold work that would be lost on close.</summary>
public interface IWorkspaceView
{
    bool IsBusy { get; }
    /// <summary>A short description of work that closing would discard (e.g. "unsaved configuration changes"), or null.</summary>
    string? UnsavedWork { get; }
}

public sealed class WorkspaceDocument(string key, string title, string resourceId, Control view, Func<Task> closeAsync)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string ResourceId { get; } = resourceId;
    public Control View { get; } = view;
    public Func<Task> CloseAsync { get; } = closeAsync;
    public bool Closing { get; internal set; }
    public bool IsBusy => View is IWorkspaceView { IsBusy: true };
    public string? UnsavedWork => (View as IWorkspaceView)?.UnsavedWork;
    public bool HasUnsavedWork => UnsavedWork != null;
}

public sealed class WorkspaceTabs : UserControl
{
    readonly Dictionary<string, DockedDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    readonly WorkspaceFactory _factory;
    readonly IRootDock _root;
    readonly DockControl _dock;
    readonly Action<string> _notify;
    DockedDocument? _active;

    public IEnumerable<WorkspaceDocument> Documents => _documents.Values.Select(d => d.Resource).ToArray();
    public int GroupCount => Groups().Count();
    public bool HasBusyDocuments => Documents.Any(d => d.IsBusy || d.Closing);
    public bool HasUnsavedWork => Documents.Any(d => d.HasUnsavedWork);

    public WorkspaceTabs(Action<string> notify)
    {
        Focusable = true;
        _notify = notify;
        _factory = new WorkspaceFactory(RequestClose);
        _root = _factory.CreateLayout();
        _factory.InitLayout(_root);
        _factory.ActiveDockableChanged += (_, e) =>
        {
            if (e.Dockable is DockedDocument document) _active = document;
        };
        _factory.FocusedDockableChanged += (_, e) =>
        {
            if (e.Dockable is DockedDocument document) _active = document;
        };
        _dock = new DockControl
        {
            Factory = _factory, Layout = _root, InitializeFactory = false, InitializeLayout = false,
        };
        Content = _dock;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public WorkspaceDocument Open(string key, Func<WorkspaceDocument> create)
    {
        if (_documents.TryGetValue(key, out var existing))
        {
            Focus(existing);
            return existing.Resource;
        }
        var resource = create();
        var document = new DockedDocument(resource);
        _documents.Add(key, document);
        var group = _active?.Owner as IDocumentDock ?? Groups().FirstOrDefault();
        if (group == null)
        {
            group = _factory.CreateDocumentDock();
            _factory.AddDockable(_root, group);
        }
        _factory.AddDockable(group, document);
        Focus(document);
        return resource;
    }

    public void Move(WorkspaceDocument resource, int direction, bool split = false)
    {
        if (!_documents.TryGetValue(resource.Key, out var document) || resource.Closing) return;
        if (split)
        {
            Split(resource, DockOperation.Right);
            return;
        }
        var groups = Groups().ToList();
        var source = document.Owner as IDocumentDock;
        var index = groups.IndexOf(source!) + Math.Sign(direction);
        if (source == null || index < 0 || index >= groups.Count)
        {
            _notify("No adjacent group. Drag a tab to an editor edge or use Split right/down.");
            return;
        }
        _factory.MoveDockable(source, groups[index], document, null);
        Focus(document);
    }

    public void Split(WorkspaceDocument resource, DockOperation operation)
    {
        if (operation is not (DockOperation.Right or DockOperation.Bottom))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (!_documents.TryGetValue(resource.Key, out var document) || resource.Closing) return;
        if (document.Owner is not IDocumentDock { VisibleDockables.Count: > 1 })
        {
            _notify("Open another tab first, then split one of them.");
            return;
        }
        if (operation == DockOperation.Right) _factory.NewHorizontalDocumentDock(document);
        else _factory.NewVerticalDocumentDock(document);
        Focus(document);
    }

    public void MoveActive(int direction, bool split = false)
    {
        if (_active != null) Move(_active.Resource, direction, split);
    }

    public void SplitActiveDown()
    {
        if (_active != null) Split(_active.Resource, DockOperation.Bottom);
    }

    public void Select(WorkspaceDocument resource)
    {
        if (_documents.TryGetValue(resource.Key, out var document)) Focus(document);
    }

    public void SwitchTab(int direction)
    {
        var tabs = (_active?.Owner as IDocumentDock)?.VisibleDockables?.OfType<DockedDocument>().ToList();
        if (tabs is not { Count: > 0 } || _active == null) return;
        var index = (tabs.IndexOf(_active) + Math.Sign(direction) + tabs.Count) % tabs.Count;
        Focus(tabs[index]);
    }

    void Focus(DockedDocument document)
    {
        _active = document;
        _factory.SetActiveDockable(document);
        _factory.SetFocusedDockable(_root, document);
        Focus();
    }

    public async Task<bool> CloseDocumentAsync(WorkspaceDocument resource, bool discardUnsaved = false)
    {
        if (!_documents.TryGetValue(resource.Key, out var document)) return true;
        if (resource.IsBusy || resource.Closing)
        {
            _notify("Wait for this tab's operation to finish before closing it.");
            return false;
        }
        if (resource.HasUnsavedWork && !discardUnsaved)
        {
            _notify($"This tab holds {resource.UnsavedWork}. Save or resend them, or explicitly confirm discarding them.");
            return false;
        }
        resource.Closing = true;
        try
        {
            await resource.CloseAsync();
            var wasActive = ReferenceEquals(_active, document);
            _factory.CompleteClose(document);
            _documents.Remove(resource.Key);
            if (wasActive)
            {
                _active = Groups().Select(g => g.ActiveDockable).OfType<DockedDocument>().FirstOrDefault();
                if (_active != null) Focus(_active);
                else Focus();
            }
            if (!Groups().Any())
            {
                var group = _factory.CreateDocumentDock();
                _factory.AddDockable(_root, group);
                _root.ActiveDockable = group;
            }
            return true;
        }
        catch (Exception ex)
        {
            _notify($"Tab could not close: {ex.Message}");
            return false;
        }
        finally { resource.Closing = false; }
    }

    public async Task<bool> CloseAllAsync(bool discardUnsaved = false)
    {
        if (HasBusyDocuments)
        {
            _notify("Wait for queue operations and tab cleanup to finish first.");
            return false;
        }
        if (HasUnsavedWork && !discardUnsaved)
        {
            _notify("Tabs still hold unsaved changes or received-and-deleted messages. Confirm discarding them first.");
            return false;
        }
        foreach (var resource in Documents)
            if (!await CloseDocumentAsync(resource, discardUnsaved)) return false;
        return true;
    }

    IEnumerable<IDocumentDock> Groups() => Descendants(_root).OfType<IDocumentDock>();

    static IEnumerable<IDockable> Descendants(IDock dock) =>
        (dock.VisibleDockables ?? []).SelectMany(d => d is IDock child ? Descendants(child).Prepend(d) : [d]);

    async void RequestClose(WorkspaceDocument resource)
    {
        if (resource.IsBusy || resource.Closing)
        {
            _notify("Wait for this tab's operation to finish before closing it.");
            return;
        }
        if (resource.HasUnsavedWork)
        {
            var flyout = new Flyout();
            flyout.Content = new StackPanel
            {
                Spacing = 10, MaxWidth = 300,
                Children =
                {
                    new TextBlock { Text = $"Discard {resource.UnsavedWork} in {resource.Title}?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    Ui.Button("Discard and close", async () =>
                    {
                        flyout.Hide();
                        await CloseDocumentAsync(resource, true);
                    }),
                },
            };
            flyout.ShowAt(this);
            return;
        }
        await CloseDocumentAsync(resource);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Tab is Ctrl on every platform; other shortcuts use the platform command key (Cmd on macOS).
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SwitchTab(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (!Ui.IsCommand(e.KeyModifiers)) return;
        else if (e.Key == Key.W && _active != null)
        {
            RequestClose(_active.Resource);
            e.Handled = true;
        }
        else if (e.Key is Key.OemBackslash or Key.OemPipe && _active != null)
        {
            Split(_active.Resource, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DockOperation.Bottom : DockOperation.Right);
            e.Handled = true;
        }
    }

    sealed class DockedDocument : Document, IRecyclingDataTemplate
    {
        public WorkspaceDocument Resource { get; }
        public DockedDocument(WorkspaceDocument resource)
        {
            Resource = resource;
            Content = resource.View;
            Id = resource.Key;
            Title = resource.Title;
            CanFloat = false;
            CanPin = false;
            CanClose = true;
            CanDrag = true;
            CanDrop = true;
        }

        public new Control? Build(object? data) => BuildView(null);
        Control? IRecyclingDataTemplate.Build(object? data, Control? existing) => BuildView(existing);

        Control BuildView(Control? existing)
        {
            var view = Resource.View;
            // Dock 11's direct-content recycler checks the logical parent, which can retain an old deferred host after docking.
            if (!ReferenceEquals(view, existing) && view.GetVisualParent() is ContentPresenter presenter)
            {
                presenter.SetCurrentValue(ContentPresenter.ContentProperty, null);
                presenter.UpdateChild();
            }
            return view;
        }
    }

    sealed class WorkspaceFactory(Action<WorkspaceDocument> requestClose) : Factory
    {
        public override IDocumentDock CreateDocumentDock() => new DocumentDock
        {
            Id = Guid.NewGuid().ToString(), Title = "Editors", IsCollapsable = true,
            CanCloseLastDockable = true, CanCreateDocument = false, CanFloat = false,
            VisibleDockables = CreateList<IDockable>(), EnableWindowDrag = false,
        };

        public override IRootDock CreateLayout()
        {
            var group = CreateDocumentDock();
            return new RootDock
            {
                Id = "Workspace", IsCollapsable = false, CanFloat = false,
                VisibleDockables = CreateList<IDockable>(group), ActiveDockable = group, DefaultDockable = group,
            };
        }

        public override void CloseDockable(IDockable dockable)
        {
            // Route native close buttons and context-menu actions through guarded async cleanup.
            if (dockable is DockedDocument document) requestClose(document.Resource);
        }

        public void CompleteClose(IDockable dockable) => base.CloseDockable(dockable);
    }
}
