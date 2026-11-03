using Avalonia.Controls;

namespace AzureWorkbench.Views;

/// <summary>Tree node that loads its children the first time it is expanded. Its context menu can reload them.</summary>
public static class LazyTreeNode
{
    public static TreeViewItem Create(object header, object? tag, Func<Task<IEnumerable<TreeViewItem>>> loadChildren)
    {
        var node = new TreeViewItem { Header = header, Tag = tag };
        node.Items.Add(new TreeViewItem { Header = "loading…" });
        var loaded = false;
        var loading = false;
        async Task LoadAsync()
        {
            if (loading) return;
            loaded = loading = true;
            try
            {
                var children = (await loadChildren()).ToList();
                node.Items.Clear();
                foreach (var c in children) node.Items.Add(c);
                if (children.Count == 0) node.Items.Add(new TreeViewItem { Header = "(none)" });
            }
            catch (Exception ex)
            {
                loaded = false; // collapse + expand to retry
                node.Items.Clear();
                var error = new TreeViewItem { Header = "⚠ " + ex.Message.Split('\n')[0].Trim() };
                ToolTip.SetTip(error, ex.Message); // full detail on hover
                var copy = new MenuItem { Header = "Copy error" };
                copy.Click += async (_, _) => await Ui.CopyAsync(error, ex.Message);
                error.ContextMenu = new ContextMenu { Items = { copy } };
                node.Items.Add(error);
            }
            finally { loading = false; }
        }
        node.PropertyChanged += async (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty || !node.IsExpanded || loaded) return;
            await LoadAsync();
        };
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += async (_, _) =>
        {
            node.Items.Clear();
            node.Items.Add(new TreeViewItem { Header = "loading…" });
            loaded = false;
            if (node.IsExpanded) await LoadAsync();
            else node.IsExpanded = true;
        };
        node.ContextMenu = new ContextMenu { Items = { refresh } };
        return node;
    }
}
