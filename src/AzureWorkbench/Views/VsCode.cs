using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;

namespace AzureWorkbench.Views;

/// <summary>VS Code "Dark+" colours layered over the Fluent theme.</summary>
static class VsCode
{
    public static readonly IBrush Editor = Brush("#1e1e1e");
    public static readonly IBrush EditorText = Brush("#d4d4d4");
    public static readonly IBrush SideBar = Brush("#252526");
    public static readonly IBrush TitleBar = Brush("#3c3c3c");
    public static readonly IBrush StatusBar = Brush("#007acc");
    public static readonly IBrush Border = Brush("#3c3c3c");
    public static readonly IBrush SectionHeader = Brush("#bbbbbb");
    public static readonly IBrush Danger = Brush("#f14c4c");
    public static readonly IBrush Warning = Brush("#cca700");
    public static readonly IBrush Success = Brush("#89d185");

    /// <summary>Glyph colours per resource type (explorer, quick open, tab headers).</summary>
    public static class Glyphs
    {
        public const string Tenant = "#c5c5c5", Subscription = "#e8ab53", ServiceBus = "#3794ff", Queue = "#4ec9b0",
            Topic = "#c586c0", SubscriptionEntity = "#b5cea8", WebApp = "#4fc1ff", FunctionApp = "#dcdcaa",
            Logs = "#9cdcfe", Config = "#d7ba7d", Deployments = "#89d185", Pinned = "#f14c4c";
    }

    public static readonly FontFamily Mono = new("Cascadia Code,Consolas,SF Mono,Menlo,Monaco,Ubuntu Mono,DejaVu Sans Mono,Liberation Mono,monospace");

    public static void Apply(Application app)
    {
        app.RequestedThemeVariant = ThemeVariant.Dark;
        app.Styles.Add(new FluentTheme
        {
            DensityStyle = DensityStyle.Compact,
            Palettes =
            {
                [ThemeVariant.Dark] = new ColorPaletteResources
                {
                    RegionColor = Color.Parse("#1e1e1e"),
                    AltHigh = Color.Parse("#1e1e1e"),
                    BaseHigh = Color.Parse("#cccccc"),
                    Accent = Color.Parse("#0e639c"),
                    ChromeLow = Color.Parse("#252526"),
                    ChromeMediumLow = Color.Parse("#252526"),
                    ChromeMedium = Color.Parse("#2d2d2d"),
                    ChromeHigh = Color.Parse("#454545"),
                    ListLow = Color.Parse("#2a2d2e"),
                    ListMedium = Color.Parse("#37373d"),
                },
            },
        });

        app.Styles.Add(new DockFluentTheme { CacheDocumentTabContent = true });
        var r = app.Resources;
        Set(r, "#252526", "DockSurfaceHeaderBrush");
        Set(r, "#d4d4d4", "DockDocumentTabPointerOverForegroundBrush");
        Set(r, "#3c3c3c", "DockBorderSubtleBrush");
        // VS Code editor tabs: inactive tabs on #2d2d2d with muted text; the active tab blends into the editor (#1e1e1e)
        // with white text and a thin accent indicator, instead of Dock's default solid accent fill.
        Set(r, "#252526", "DockDocumentTabStripBackgroundBrush");
        Set(r, "#2d2d2d", "DockTabBackgroundBrush");
        Set(r, "#323233", "DockTabHoverBackgroundBrush");
        Set(r, "#1e1e1e", "DockTabActiveBackgroundBrush", "DockApplicationAccentBrushLow", "DockSurfaceHeaderActiveBrush");
        Set(r, "#ffffff", "DockTabActiveForegroundBrush", "DockTabSelectedForegroundBrush", "DockDocumentTabSelectedForegroundBrush",
            "DockDocumentTabCloseSelectedForegroundBrush", "DockApplicationAccentForegroundBrush");
        Set(r, "#969696", "DockTabForegroundBrush");
        Set(r, "#007acc", "DockTabActiveIndicatorBrush", "DockApplicationAccentBrushIndicator", "DockThemeAccentBrush");
        Set(r, "#3c3c3c", "DockTabCloseHoverBackgroundBrush");
        r["DockDocumentTabItemMinHeight"] = 34.0;
        r["DockDocumentTabStripMinHeight"] = 34.0;
        r["ControlCornerRadius"] = new CornerRadius(2);
        // Expanders (e.g. the message composer) use the side-bar surface rather than Fluent's black.
        Set(r, "#252526", "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed",
            "ExpanderHeaderBackgroundDisabled", "ExpanderContentBackground");
        Set(r, "#3c3c3c", "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderBrushPointerOver", "ExpanderHeaderBorderBrushPressed",
            "ExpanderHeaderBorderBrushDisabled", "ExpanderContentBorderBrush");
        Set(r, "#cccccc", "ExpanderHeaderForeground", "ExpanderHeaderForegroundPointerOver", "ExpanderHeaderForegroundPressed");
        Set(r, "#3a3d41", "ButtonBackground");
        Set(r, "#45494e", "ButtonBackgroundPointerOver");
        Set(r, "#2f3236", "ButtonBackgroundPressed");
        Set(r, "#0e639c", "AccentButtonBackground");
        Set(r, "#1177bb", "AccentButtonBackgroundPointerOver");
        Set(r, "#0b5385", "AccentButtonBackgroundPressed");
        Set(r, "#ffffff", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed");
        Set(r, "#00000000", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed");
        Set(r, "#3c3c3c", "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        Set(r, "#007fd4", "TextControlBorderBrushFocused");
        Set(r, "#cccccc", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        // list/tree hover and selection
        Set(r, "#2a2d2e", "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush",
            "TreeViewItemBackgroundPointerOver", "TreeViewItemBackgroundPressed");
        Set(r, "#04395e", "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightListAccentHighBrush", "TreeViewItemBackgroundSelected",
            "TreeViewItemBackgroundSelectedPointerOver", "TreeViewItemBackgroundSelectedPressed");

        // Panel-style inner tabs (TabControl.panel): small uppercase headers with an underline on the active tab.
        r["TabItemHeaderFontSize"] = 11.0;
        r["TabItemMinHeight"] = 30.0;
        Set(r, "#e7e7e7", "TabItemHeaderSelectedPipeFill");
        Set(r, "#969696", "TabItemHeaderForegroundUnselected", "TabItemHeaderForegroundUnselectedPointerOver");
        Set(r, "#e7e7e7", "TabItemHeaderForegroundSelected", "TabItemHeaderForegroundSelectedPointerOver");
        app.Styles.Add(new Style(x => x.OfType<TabControl>().Class("panel").Descendant().OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 0)),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.Normal),
                new Setter(Layoutable.MinHeightProperty, 30.0),
            },
        });
        // Destructive button style (Button.danger), used for purge / receive & delete confirmations.
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("danger"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush("#a1260d")), new Setter(TemplatedControl.ForegroundProperty, Brushes.White) },
        });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("danger").Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>())
        {
            Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brush("#be1100")), new Setter(Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Brushes.White) },
        });
        // Compact VS Code-like buttons.
        app.Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)), new Setter(TemplatedControl.FontSizeProperty, 12.0) },
        });
        // Quiet toolbar buttons (Button.tool): text-only until hovered, like VS Code's editor actions.
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("tool"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.FontSizeProperty, 11.0),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 2)), new Setter(TemplatedControl.ForegroundProperty, Brush("#c5c5c5")),
            },
        });

        // VS Code "sash": invisible until hovered or dragged
        app.Styles.Add(new Style(x => x.OfType<GridSplitter>()) { Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) } });
        app.Styles.Add(new Style(x => Selectors.Or(x.OfType<GridSplitter>().Class(":pointerover"), x.OfType<GridSplitter>().Class(":pressed")))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush("#007fd4")) },
        });
    }

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    static void Set(IResourceDictionary r, string hex, params string[] keys)
    {
        foreach (var k in keys) r[k] = Brush(hex);
    }
}
