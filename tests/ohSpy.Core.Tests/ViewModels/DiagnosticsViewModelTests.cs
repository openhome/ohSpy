namespace ohSpy.Core.Tests.ViewModels;

using System;
using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 5.1 — DiagnosticsViewModel (AC-5.1.3/.4/.6/.7/.8/.10/.11/.12). The VM is passive: it binds the
/// SAME live ring instance (AC-8.2) and couples its MinSeverity control to the runtime emitter gate (Q1).
/// </summary>
public class DiagnosticsViewModelTests
{
    private static DiagnosticEntry Entry(DiagnosticContext ctx = default, DiagSeverity sev = DiagSeverity.Information) =>
        new(DateTime.UtcNow, sev, "test.category", "msg", ctx);

    private static DiagnosticLevelGate Gate(DiagSeverity seed = DiagSeverity.Information) =>
        new(Options.Create(new DiagnosticOptions { MinSeverity = seed }));

    // ─── AC-5.1.3: Entries is the SAME instance as the sink's ───────────────

    [Fact]
    [Trait("ac", "AC-5.1.3")]
    public void Entries_IsSameInstanceAsRingSink()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate());

        ReferenceEquals(vm.Entries, sink.Entries).Should().BeTrue("AC-8.2: no copy, no view layer");
    }

    // ─── AC-5.1.4: MinSeverity default + observability + gate write-through ──

    [Fact]
    [Trait("ac", "AC-5.1.4")]
    public void MinSeverity_DefaultIsSeededFromGate()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate(DiagSeverity.Warning));

        vm.MinSeverity.Should().Be(DiagSeverity.Warning, "the VM seeds MinSeverity from the gate (which seeds from options)");
    }

    [Fact]
    [Trait("ac", "AC-5.1.4")]
    public void MinSeverity_DefaultIsInformation_WhenGateSeededInformation()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate(DiagSeverity.Information));

        vm.MinSeverity.Should().Be(DiagSeverity.Information, "D8 default");
    }

    [Fact]
    [Trait("ac", "AC-5.1.4")]
    public void MinSeverity_RaisesPropertyChanged()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.MinSeverity = DiagSeverity.Error;

        raised.Should().Contain(nameof(DiagnosticsViewModel.MinSeverity));
    }

    [Fact]
    [Trait("ac", "AC-5.1.10")]
    public void MinSeveritySetter_WritesThroughToGate()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var gate = Gate(DiagSeverity.Information);
        var vm = new DiagnosticsViewModel(sink, gate);

        vm.MinSeverity = DiagSeverity.Verbose;
        gate.MinSeverity.Should().Be(DiagSeverity.Verbose, "Q1: the setter drives the runtime emitter gate");

        vm.MinSeverity = DiagSeverity.Error;
        gate.MinSeverity.Should().Be(DiagSeverity.Error);
    }

    [Fact]
    [Trait("ac", "AC-5.1.4")]
    public void SelectableSeverities_AreOrderedLeastToMostSevere()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate());

        vm.SelectableSeverities.Should().Equal(
            DiagSeverity.Verbose, DiagSeverity.Information, DiagSeverity.Warning, DiagSeverity.Error);
    }

    // ─── AC-5.1.11: integration — 100 entries, newest-first + resolution ────

    [Fact]
    [Trait("ac", "AC-5.1.11")]
    public void HundredEntries_NewestFirst_WithResolvedLabels()
    {
        var lookup = new StaticIdentityLookup("Linn DS");
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), lookup);
        var vm = new DiagnosticsViewModel(sink, Gate(DiagSeverity.Verbose));

        var udn = $"uuid:{Guid.NewGuid()}";
        for (int i = 0; i < 100; i++)
        {
            var sev = (DiagSeverity)(i % 4);
            sink.Push(new DiagnosticEntry(
                DateTime.UtcNow, sev, "cat", $"m{i}",
                new DiagnosticContext { DeviceUuid = udn, Url = "http://192.168.1.1:8008/x" }));
        }

        vm.Entries.Count.Should().Be(100);
        // Newest-first: the LAST pushed (m99) is at index 0.
        vm.Entries[0].Entry.Message.Should().Be("m99");
        vm.Entries[99].Entry.Message.Should().Be("m0");
        // Resolution (AC-8.3 friendly name / AC-8.4 host:port) holds across all.
        vm.Entries[0].IdentityLabel.Should().Be("Linn DS");
        vm.Entries[0].EndpointLabel.Should().Be("192.168.1.1:8008");
    }

    // ─── AC-5.1.12: MANDATORY marshalling guard (DeferredUiDispatcher) ───────

    [Fact]
    [Trait("ac", "AC-5.1.12")]
    public void RingPrepend_IsAppliedThroughUiDispatcherPost()
    {
        // Retro Action H: prove the sink's prepend goes THROUGH IUiDispatcher.Post — Entries is
        // unchanged until Drain() runs the queued action.
        var dispatcher = new DeferredUiDispatcher();
        var sink = new DiagnosticRingSink(dispatcher, new StaticIdentityLookup(null));
        var vm = new DiagnosticsViewModel(sink, Gate());

        sink.Push(Entry());

        vm.Entries.Count.Should().Be(0, "the prepend must be queued via Post, not applied on the calling thread");
        dispatcher.PostCount.Should().Be(1);

        dispatcher.Drain();

        vm.Entries.Count.Should().Be(1, "Drain() runs the queued Post → the row is now present");
        vm.Entries[0].Entry.Message.Should().Be("msg");
    }

    // ─── DiagnosticRow.TimestampDisplay formatting (UTC HH:mm:ss.fff) ────────

    [Fact]
    [Trait("fr", "FR-041")]
    public void TimestampDisplay_FormatsUtcHhMmSsFff_Invariant()
    {
        var ts = new DateTime(2026, 6, 4, 13, 7, 5, 123, DateTimeKind.Utc);
        var row = new DiagnosticRow(
            new DiagnosticEntry(ts, DiagSeverity.Information, "c", "m", default), "—", "—");

        row.TimestampDisplay.Should().Be("13:07:05.123");
        // Invariant-culture: not affected by the current thread culture.
        row.TimestampDisplay.Should().Be(ts.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait("fr", "FR-041")]
    public void SeverityLabel_IsEnumName()
    {
        var row = new DiagnosticRow(
            new DiagnosticEntry(DateTime.UtcNow, DiagSeverity.Warning, "c", "m", default), "—", "—");
        row.SeverityLabel.Should().Be("Warning");
    }

    // ─── Test doubles ───────────────────────────────────────────────────────

    private sealed class StaticIdentityLookup(string? name) : IDiagnosticIdentityLookup
    {
        public string? TryGetFriendlyName(string udn) => name;
    }
}
