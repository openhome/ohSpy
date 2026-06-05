namespace ohSpy.Core.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Collections;
using ohSpy.Core.Diagnostics;

/// <summary>
/// ViewModel for the FR-041 Diagnostics viewer (Story 5.1). PASSIVE: it binds the live diagnostic
/// ring (<see cref="IDiagnosticRingSink.Entries"/>) directly — the SAME instance, no copy, no view
/// layer (AC-8.2) — and exposes the single operator <see cref="MinSeverity"/> control.
/// <para>
/// <b>MinSeverity has TWO coupled meanings (Q1, Project Lead 2026-06-04):</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Emitter gate (capture):</b> the setter writes through to <see cref="IDiagnosticLevelGate"/>,
///     so flipping to <see cref="DiagSeverity.Verbose"/> turns the firehose ON (lower-severity entries
///     start entering the ring) and raising it turns it OFF (those entries are never created — AC-8.7).
///   </item>
///   <item>
///     <b>View filter (display):</b> the App-side row template hides already-captured rows below
///     <see cref="MinSeverity"/> (needed because raising the gate leaves earlier lower-severity rows in
///     the ring — the ring is NOT mutated or copied, AC-8.2).
///   </item>
/// </list>
/// <para>Runtime-only — neither the gate nor the filter persists across restart (PRD §7 Non-Goal).</para>
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IDiagnosticRingSink _ringSink;
    private readonly IDiagnosticLevelGate _gate;

    public DiagnosticsViewModel(IDiagnosticRingSink ringSink, IDiagnosticLevelGate gate)
    {
        ArgumentNullException.ThrowIfNull(ringSink);
        ArgumentNullException.ThrowIfNull(gate);
        _ringSink = ringSink;
        _gate = gate;
        // Seed the VM's control from the gate (which was itself seeded from the configured
        // DiagnosticOptions.MinSeverity default) so the viewer reflects the live capture level.
        _minSeverity = gate.MinSeverity;
    }

    /// <summary>
    /// The live diagnostic ring — the SAME <see cref="BoundedObservableCollection{T}"/> instance the
    /// sink populates (AC-8.2: no copy, no view layer). Newest-first; FR-041 cap = 5000.
    /// </summary>
    public BoundedObservableCollection<DiagnosticRow> Entries => _ringSink.Entries;

    /// <summary>
    /// The operator's minimum-severity control (D8 — runtime-flippable, NOT persisted). Default seeded
    /// from the gate. Changing it drives BOTH the emitter gate (capture) and the view filter (display).
    /// </summary>
    [ObservableProperty]
    private DiagSeverity _minSeverity;

    /// <summary>
    /// The selectable severities the App binds for the chip/selector affordance (AC-5.1.4). Ordered
    /// least-to-most severe so the operator reads left-to-right as "show this and above".
    /// </summary>
    public IReadOnlyList<DiagSeverity> SelectableSeverities { get; } = new[]
    {
        DiagSeverity.Verbose,
        DiagSeverity.Information,
        DiagSeverity.Warning,
        DiagSeverity.Error,
    };

    // Q1 (capture half): write the chosen level through to the runtime emitter gate so the firehose
    // flips on/off. The view-filter (display half) is applied App-side off this same property's change
    // notification. Pure Core — no Visibility/Brush here (Pattern 2).
    partial void OnMinSeverityChanged(DiagSeverity value) => _gate.MinSeverity = value;
}
