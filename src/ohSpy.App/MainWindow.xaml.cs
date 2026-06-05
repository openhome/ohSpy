using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ohSpy.Core.ViewModels;

namespace ohSpy.App;

// Pattern 13: constructor-only code-behind; all logic in VM — EXCEPT the smart auto-follow
// scroll handlers below (FR-055), which are pure view mechanics (scroll-offset bookkeeping
// against a live ScrollViewer) and cannot live in Core or be unit-tested headlessly. The
// testable state (IsAtTop) lives in SsdpLogViewModel; these handlers only read/write it.
public sealed partial class MainWindow : Window
{
    // One log row is a single Consolas line + 2px padding top/bottom. ~24px is a safe
    // "near the top" threshold (AC-2.7.5 — within one row of the top counts as at-top).
    private const double AtTopThresholdPx = 24.0;

    // Exposed as a typed property so x:Bind in XAML can reference it at compile time.
    public ShellViewModel ViewModel { get; }

    public MainWindow(ShellViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // ── Smart auto-follow (FR-055) — view mechanics only, no business logic. ──
        // The window and the ShellViewModel share the app's lifetime (both die at shutdown),
        // so these subscriptions need no explicit teardown.
        LogScrollViewer.ViewChanged += OnLogViewChanged;
        ViewModel.SsdpLog.Entries.CollectionChanged += OnLogEntriesChanged;

        // Assign each TreeViewItem's child ItemsSource from its node's Children as containers
        // realize (see XAML note: WinUI leaves the container DataContext null, so the declarative
        // binding can't). LayoutUpdated fires after each realization pass; the assignment is
        // coalesced and idempotent so it settles after one pass per new container.
        DeviceTreeView.LayoutUpdated += OnTreeLayoutUpdated;
    }

    private bool _assignPending;

    // Children of an expandable node (device → services, service → actions); null for leaves.
    private static ObservableCollection<INodeViewModel>? NodeChildren(object? node) => node switch
    {
        DeviceNodeViewModel d => d.Children,
        ServiceNodeViewModel s => s.Children,
        _ => null,
    };

    private void OnTreeLayoutUpdated(object? sender, object e)
    {
        if (_assignPending)
        {
            return;
        }

        _assignPending = true;
        // Defer off the layout pass so we never mutate container ItemsSource during layout.
        DispatcherQueue.TryEnqueue(() =>
        {
            _assignPending = false;
            AssignContainerSources(ViewModel.DeviceTree.Devices);
        });
    }

    private void AssignContainerSources(System.Collections.IEnumerable nodes)
    {
        foreach (var node in nodes)
        {
            var children = NodeChildren(node);
            if (children is null)
            {
                // Leaf (action) — no children, so no chevron. BUT WinUI recycles TreeViewItem
                // containers: a container reused from a service row (which we gave an ItemsSource,
                // hence a chevron) keeps that ItemsSource and shows a PHANTOM chevron on the action.
                // Clear it so leaves render chevron-free. The set-branch below re-assigns if this same
                // container is later recycled back to an expandable row.
                if (DeviceTreeView.ContainerFromItem(node) is TreeViewItem leaf && leaf.ItemsSource is not null)
                {
                    leaf.ItemsSource = null;
                }
                continue;
            }

            // Only realized containers are returned (collapsed subtrees yield null) — so this
            // recurses lazily, assigning each level's containers once they exist.
            if (DeviceTreeView.ContainerFromItem(node) is TreeViewItem container)
            {
                if (container.ItemsSource is null)
                {
                    container.ItemsSource = children;
                }

                AssignContainerSources(children);
            }
        }
    }

    // After a node's lazy build replaces its Children (placeholder → services / actions), the
    // TreeViewItem keeps the ItemsSource it snapshotted when the container first realized — often the
    // placeholder-only collection (AssignContainerSources assigns once, and whether that lands before
    // or after the build is a layout-timing race). Force the displayed container to re-read the live
    // children: null-then-reassign drops the stale snapshot so WinUI re-binds to the rebuilt collection.
    private void RebindChildren(object node, ObservableCollection<INodeViewModel> children)
    {
        if (DeviceTreeView.ContainerFromItem(node) is TreeViewItem container)
        {
            container.ItemsSource = null;
            container.ItemsSource = children;
        }
    }

    // ── Tree lazy-load + expand discoverability — view mechanics only, no business logic
    //    (Pattern 13 documented exception, like the auto-follow handlers above). The Story 2.6
    //    lazy load (build service list / fetch SCPD) hangs off the node VM's IsExpanded, which
    //    nothing set from the UI. These element-level handlers (NOT style/template bindings,
    //    which crashed against the null-DataContext containers) close that gap. ──

    // Fires when the operator expands a row (chevron or programmatic). Set the node VM's
    // IsExpanded to trigger OnIsExpandedChanged. Deferred via the dispatcher so we never mutate
    // the bound Children collection synchronously inside the TreeView's expand pass.
    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        switch (args.Item)
        {
            case DeviceNodeViewModel dev when !dev.IsExpanded:
                DispatcherQueue.TryEnqueue(() =>
                {
                    dev.IsExpanded = true; // synchronous: builds the service list into dev.Children
                    RebindChildren(dev, dev.Children);
                });
                break;
            case ServiceNodeViewModel svc when !svc.IsExpanded:
                DispatcherQueue.TryEnqueue(() =>
                {
                    svc.IsExpanded = true;
                    RebindChildren(svc, svc.Children);
                });
                break;
        }
    }

    // Double-click a row to toggle its expansion (the chevron alone is easy to miss). Find the
    // node's TreeViewItem container and flip IsExpanded — expanding raises OnTreeExpanding which
    // drives the lazy load. Also re-asserts the container's ItemsSource as a safety net in case
    // the ItemContainerStyle {Binding Children} did not resolve (null-DataContext container).
    private void OnTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext;

        // Story 3.2 (AC-3.2.4 #14): double-clicking an ACTION row opens the invocation popup.
        // Actions are leaves (no expansion to toggle) — route straight to the command. Same
        // null-DataContext-safe item lookup as the expand branch (WinUI TreeView quirk).
        if (item is ActionNodeViewModel act)
        {
            // Open the invocation popup. The popup-behind-on-double-click race (the shell reclaims
            // foreground after the second click's mouse-up, dropping the popup behind it post-A31) is
            // handled in InvocationPopupLauncher, which re-asserts the popup on top at Low priority
            // once the input event has unwound. A synchronous Activate alone loses that race.
            act.OpenInvocationPopupCommand.Execute(null);
            return;
        }

        if (item is DeviceNodeViewModel or ServiceNodeViewModel &&
            DeviceTreeView.ContainerFromItem(item) is TreeViewItem container)
        {
            if (container.ItemsSource is null)
            {
                if (item is DeviceNodeViewModel dev) container.ItemsSource = dev.Children;
                else if (item is ServiceNodeViewModel svc) container.ItemsSource = svc.Children;
            }
            container.IsExpanded = !container.IsExpanded;
        }
    }

    private void OnLogViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Update IsAtTop from the operator's scroll position. The VM property drives whether
        // the next arrival re-anchors to the top (FR-055).
        ViewModel.SsdpLog.IsAtTop = LogScrollViewer.VerticalOffset <= AtTopThresholdPx;
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Only react to a newest-row prepend (Add at index 0). Remove (tail eviction) and
        // Reset (Clear) need no scroll adjustment.
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        if (ViewModel.SsdpLog.IsAtTop)
        {
            // Parked at the top: keep the newest row in view (anchor to offset 0).
            LogScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        }
        else
        {
            // Scrolled away reading history: a top prepend pushes existing content down by one
            // row, so add ~one row to the offset to keep the SAME item under the viewport — do
            // NOT yank to the top (FR-055). Rows are uniform single lines, so a fixed delta is
            // accurate enough. (Variable-height rows are out of scope for this log.)
            LogScrollViewer.ChangeView(
                null, LogScrollViewer.VerticalOffset + AtTopThresholdPx, null,
                disableAnimation: true);
        }
    }

    // ── Story 5.2: View → Network adapter menu — view mechanics only (Pattern 13 documented
    //    exception, like the auto-follow + tree handlers). The adapter list is runtime-variable, so the
    //    submenu is REBUILT each time the View flyout opens (AC-5.2.1). Business logic — enumeration,
    //    the current-adapter check, and the atomic rebind — lives in ShellViewModel. ──
    private void OnViewMenuOpening(object? sender, object e)
    {
        var items = NetworkAdapterMenu.Items;
        items.Clear();

        var adapters = ViewModel.EnumerateAdapters();
        var switching = ViewModel.IsSwitching;

        // AC-5.2.1: with zero or one eligible adapter there is nowhere to switch TO — show a single
        // disabled hint, but the menu still opens.
        if (adapters.Count <= 1)
        {
            items.Add(new MenuFlyoutItem { Text = "No other adapters available", IsEnabled = false });
            return;
        }

        foreach (var adapter in adapters)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"{adapter.Name}  ({adapter.IPv4.ToString()})",
                GroupName = "NetworkAdapter", // mutually exclusive (AC-5.2.1)
                IsChecked = ViewModel.IsCurrentAdapter(adapter),
                IsEnabled = !switching,        // AC-5.2.9: disabled while a switch is in flight
            };

            // Fire-and-forget the switch (the launcher/command precedent); SwitchAdapterAsync handles its
            // own exceptions + the same-adapter no-op + the re-entrancy guard internally.
            var chosen = adapter;
            item.Click += (_, _) => _ = ViewModel.SwitchAdapterAsync(chosen);
            items.Add(item);
        }
    }
}
