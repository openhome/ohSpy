namespace ohSpy.Core.Tests.Discovery;

using System.Net;
using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.2 — <c>AdapterScope</c>: launch-default selection (FR-048), startup bind +
/// M-SEARCH (FR-004), Decision-7 token linkage, NFR-R5 zero-adapter degradation, and
/// the FR-050-budgeted teardown shape. Drives a <see cref="FakeSsdpTransport"/> (no
/// sockets). Every AC-traceable test carries <c>[Trait("ac", "AC-2.2.&lt;n&gt;")]</c>.
/// </summary>
public sealed class AdapterScopeTests
{
    private static NetworkAdapterEnumerator EnumeratorWith(params AdapterCandidate[] candidates) =>
        new(new StubNetworkInterfaceSource(candidates));

    private static AdapterScope Scope(
        INetworkAdapterEnumerator enumerator,
        FakeSsdpTransport transport,
        CapturingDiagnosticEmitter diag,
        CancellationToken appToken = default) =>
        // A23: the scope constructs+owns its transport via a factory. The test pre-creates the fake so
        // it can assert against it, then hands it back through a single-shot factory.
        new(enumerator, () => transport, diag, appToken);

    [Fact]
    [Trait("ac", "AC-2.2.4")]
    [Trait("ac", "AC-2.2.5")]
    public async Task StartAsync_OneAdapter_StartsTransportWithSelectedIpAndMSearch_AC224_AC225()
    {
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        var enumerator = EnumeratorWith(StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50"));
        await using var scope = Scope(enumerator, transport, diag);

        await scope.StartAsync();

        transport.StartCallCount.Should().Be(1);
        transport.StartedWith.Should().Be(IPAddress.Parse("192.168.1.50"));
        transport.MSearchCallCount.Should().Be(1);
        transport.MSearchMx.Should().Be(TimeSpan.FromSeconds(5));
        scope.CurrentAdapterIPv4.Should().Be(IPAddress.Parse("192.168.1.50"));
    }

    [Fact]
    [Trait("ac", "AC-2.2.4")]
    public async Task StartAsync_ManyAdapters_SelectsFirstEligible_AC224()
    {
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        var enumerator = EnumeratorWith(
            StubNetworkInterfaceSource.Eligible("First", "10.0.0.1"),
            StubNetworkInterfaceSource.Eligible("Second", "10.0.0.2"));
        await using var scope = Scope(enumerator, transport, diag);

        await scope.StartAsync();

        scope.CurrentAdapterIPv4.Should().Be(IPAddress.Parse("10.0.0.1"));
        transport.StartedWith.Should().Be(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    [Trait("ac", "AC-2.2.6")]
    public async Task StartAsync_ZeroAdapters_EmitsWarningDoesNotStart_AC226()
    {
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        await using var scope = Scope(EnumeratorWith(), transport, diag);

        await scope.StartAsync();

        transport.StartCallCount.Should().Be(0);
        transport.MSearchCallCount.Should().Be(0);
        scope.CurrentAdapterIPv4.Should().BeNull();

        diag.Entries.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Severity = "Warning",
                Category = DiagCategories.AdapterSwitch,
                Message = "no eligible adapters at startup",
            });
    }

    [Fact]
    [Trait("ac", "AC-2.2.3")]
    public async Task AdapterToken_LinkedToAppToken_AC223()
    {
        using var appCts = new CancellationTokenSource();
        var diag = new CapturingDiagnosticEmitter();
        await using var scope = Scope(EnumeratorWith(), new FakeSsdpTransport(), diag, appCts.Token);

        scope.AdapterToken.IsCancellationRequested.Should().BeFalse();
        await appCts.CancelAsync();
        scope.AdapterToken.IsCancellationRequested.Should().BeTrue(
            "the adapter CTS is linked to the app token (Decision 7)");
    }

    [Fact]
    [Trait("ac", "AC-2.2.8")]
    public async Task DisposeAsync_CancelsTokenAndDisposesTransport_AC228()
    {
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        var scope = Scope(EnumeratorWith(StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")),
            transport, diag);
        await scope.StartAsync();

        var token = scope.AdapterToken;
        await scope.DisposeAsync();

        token.IsCancellationRequested.Should().BeTrue();
        transport.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.2.8")]
    public async Task DisposeAsync_TransportNeverStarted_StillDisposesOwnedTransport_AC228()
    {
        // A23 ownership change: the scope CONSTRUCTS+OWNS its transport via the factory, so it must
        // dispose it on teardown even when no adapter bound (else the constructed transport would leak).
        // The unstarted SsdpTransport.DisposeAsync is idempotent + leak-free (null sockets).
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        var scope = Scope(EnumeratorWith(), transport, diag); // zero adapters ⇒ never bound
        await scope.StartAsync();

        await scope.DisposeAsync();

        transport.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.2.8")]
    public async Task DisposeAsync_CalledTwice_Idempotent_AC228()
    {
        var transport = new FakeSsdpTransport();
        var diag = new CapturingDiagnosticEmitter();
        var scope = Scope(EnumeratorWith(StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")),
            transport, diag);
        await scope.StartAsync();

        await scope.DisposeAsync();
        await scope.DisposeAsync(); // second call is a no-op

        transport.DisposeCallCount.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.2.8")]
    public async Task DisposeAsync_TeardownExceedsBudget_EmitsTimeoutWarning_AC228()
    {
        var transport = new FakeSsdpTransport { TeardownDelay = TimeSpan.FromMilliseconds(500) };
        var diag = new CapturingDiagnosticEmitter();
        // Short budget via the test-only ctor so we don't wait the full 2 s.
        var scope = new AdapterScope(
            EnumeratorWith(StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")),
            () => transport,
            diag,
            switchBudget: TimeSpan.FromMilliseconds(50),
            appToken: default);
        await scope.StartAsync();

        await scope.DisposeAsync();

        diag.Entries.Should().Contain(e =>
            e.Severity == "Warning" && e.Category == DiagCategories.AdapterSwitchTimeout);

        // Cancel the fake's lingering delay so the background continuation does not
        // outlive the test (prevents orphaned Task.Delay after the 50 ms budget fires).
        await transport.TeardownCts.CancelAsync();
    }
}
