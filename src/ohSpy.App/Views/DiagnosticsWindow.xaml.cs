namespace ohSpy.App.Views;

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.ViewModels;

/// <summary>
/// Diagnostics viewer window (Story 5.1, FR-041). Pattern 13: constructor-only code-behind. The only
/// logic here is the App-side concern Core cannot carry: the MinSeverity VIEW FILTER (the display half
/// of Q1 — the emitter-gate capture half lives in the VM/gate seam). Raising the gate stops NEW
/// lower-severity entries entering the ring, but rows ALREADY captured below the threshold stay in the
/// ring (AC-8.2 forbids mutating/copying it) — so each realized row's <see cref="UIElement.Visibility"/>
/// is set from <c>row.Entry.Severity &gt;= ViewModel.MinSeverity</c>. Per-row Visibility (mechanism #1
/// in the story Dev Notes) keeps <c>ViewModel.Entries</c> the SAME ring instance.
/// <para>
/// <see cref="UIElement.Visibility"/> is an App-tree concern (Pattern 2 forbids it in Core), so it is
/// applied here, not in the VM. The <see cref="Window.Closed"/> handler unsubscribes the VM
/// PropertyChanged hook (VSTHRD100 synchronous void).
/// </para>
/// </summary>
public sealed partial class DiagnosticsWindow : Window
{
    // Exposed as a typed property so x:Bind resolves at compile time (MainWindow/PropertiesWindow precedent).
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsWindow(DiagnosticsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Diagnostics";

        // Re-apply the per-row view filter whenever the operator changes the minimum severity.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed; // sync void (VSTHRD100)
    }

    // As each row container is realized (virtualisation): (1) set its DataContext so the one classic
    // {Binding} in the row template (the severity-colour converter — x:Bind can't carry a StaticResource
    // converter under a Window root) resolves; (2) set its visibility against the current filter.
    // ItemsRepeater leaves DataContext null and feeds the row via the x:DataType compiled bindings, so the
    // row data is fetched by index from the source.
    private void OnRowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement fe && args.Index < ViewModel.Entries.Count)
        {
            var row = ViewModel.Entries[args.Index];
            fe.DataContext = row; // makes the severity-colour {Binding} resolve
            fe.Visibility = IsVisibleAtFilter(row) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticsViewModel.MinSeverity))
        {
            ReapplyFilterToRealizedRows();
        }
    }

    // Walk the currently-realized containers and re-evaluate visibility. Newly-realized rows pick the
    // filter up via OnRowPrepared; this catches the rows already on-screen when MinSeverity changes.
    private void ReapplyFilterToRealizedRows()
    {
        var count = ViewModel.Entries.Count;
        for (var i = 0; i < count; i++)
        {
            // Same DataContext-is-null caveat as OnRowPrepared — read the row by index, not DataContext.
            if (RowsRepeater.TryGetElement(i) is FrameworkElement fe)
            {
                fe.Visibility = IsVisibleAtFilter(ViewModel.Entries[i]) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private bool IsVisibleAtFilter(DiagnosticRow row) => row.Entry.Severity >= ViewModel.MinSeverity;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
