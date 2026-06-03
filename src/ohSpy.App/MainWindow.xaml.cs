using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
