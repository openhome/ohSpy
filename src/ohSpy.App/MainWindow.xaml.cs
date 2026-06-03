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
                continue; // leaf — no chevron / children
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
                DispatcherQueue.TryEnqueue(() => dev.IsExpanded = true);
                break;
            case ServiceNodeViewModel svc when !svc.IsExpanded:
                DispatcherQueue.TryEnqueue(() => svc.IsExpanded = true);
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
}
