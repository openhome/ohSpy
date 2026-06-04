namespace ohSpy.Core.Tests.Devices;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.3 — <see cref="EagerDescriptionDispatcher"/> canonical fetch flow (Decision 9).
/// Drives the dispatcher through the registry's auto-fetch subscription with a
/// <see cref="StubUpnpHttpClient"/> + <see cref="StubDeviceDescriptionParser"/> and an
/// <see cref="InlineUiDispatcher"/> (posts run inline → outcomes are observable right after
/// the synchronous <c>OnAlive</c> returns). Covers AC-9.x-flow / 9.6 / 9.7 / 9.x-fail and
/// the AC-7.2 per-device byebye drill, plus the NFR-P6 concurrency cap and UdnMatches.
/// </summary>
public sealed class EagerDescriptionDispatcherTests
{
    private sealed record Harness(
        DeviceRegistry Registry,
        StubUpnpHttpClient Http,
        StubDeviceDescriptionParser Parser,
        CapturingDiagnosticEmitter Diag,
        EagerDescriptionDispatcher Dispatcher);

    private static Harness NewHarness()
    {
        var ui = new InlineUiDispatcher();
        var registry = new DeviceRegistry(ui);
        var http = new StubUpnpHttpClient();
        var parser = new StubDeviceDescriptionParser();
        var diag = new CapturingDiagnosticEmitter();
        var dispatcher = new EagerDescriptionDispatcher(http, parser, ui, registry, diag);
        return new Harness(registry, http, parser, diag, dispatcher);
    }

    private static Uri Loc(int i = 0) => new($"http://192.0.2.10:49152/desc{i}.xml");

    private static string NewUdn() => $"uuid:{Guid.NewGuid()}";

    private static void Alive(DeviceRegistry r, string udn, Uri url, CancellationToken adapterToken = default) =>
        r.OnAlive(udn, url, DateTime.UtcNow, "Linn/1.0", TimeSpan.FromSeconds(1800), "1", "1", adapterToken);

    // ─── UdnMatches (AC-9.6 normalisation; Amendment A30 — string/string, OrdinalIgnoreCase) ──
    // Regression (b): GUID-cased UDNs match across case; opaque UDNs match themselves / mismatch
    // others; the uuid:-prefix is stripped from both sides so a prefix asymmetry still matches.

    [Theory]
    [Trait("ac", "AC-9.6")]
    // GUID-cased, OrdinalIgnoreCase (preserves the old Guid-equality semantics):
    [InlineData("uuid:2fac1234-31f8-11b4-a222-08002b34c003", "uuid:2fac1234-31f8-11b4-a222-08002b34c003", true)]
    [InlineData("uuid:F7DC20E5-1234-5678-ABCD-EF0123456789", "uuid:f7dc20e5-1234-5678-abcd-ef0123456789", true)]
    [InlineData("uuid:00000000-0000-0000-0000-000000000001", "uuid:2fac1234-31f8-11b4-a222-08002b34c003", false)]
    // Opaque (non-RFC-4122) UDNs:
    [InlineData("uuid:linn-ds-0001", "uuid:linn-ds-0001", true)]
    [InlineData("uuid:linn-ds-0001", "uuid:linn-ds-0002", false)]
    // Prefix asymmetry — both sides strip uuid: before comparing:
    [InlineData("linn-ds-0001", "uuid:linn-ds-0001", true)]
    [InlineData("uuid:LINN-DS-0001", "linn-ds-0001", true)]
    public void UdnMatches_OrdinalIgnoreCase_PrefixStripped_AC96(string descUdn, string registeredUdn, bool expected)
    {
        EagerDescriptionDispatcher.UdnMatches(descUdn, registeredUdn).Should().Be(expected);
    }

    // ─── Canonical flows ───────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-9.flow")]
    public void Fetch_Happy_MarksLoaded_RaisesDeviceLoaded()
    {
        var h = NewHarness();
        var udn = NewUdn();
        h.Parser.Responder = _ => StubDeviceDescriptionParser.Description(udn, "Living Room");
        var loaded = new List<RegistryEntry>();
        h.Registry.DeviceLoaded += loaded.Add;

        Alive(h.Registry, udn, Loc()); // synchronous stub ⇒ fetch completes inline

        h.Registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Loaded);
        entry.Description!.FriendlyName.Should().Be("Living Room");
        h.Registry.Loaded.Should().ContainSingle().Which.Udn.Should().Be(udn);
        loaded.Should().ContainSingle().Which.Should().BeSameAs(entry);
        h.Http.RequestedUrls.Should().ContainSingle().Which.Should().Be(Loc());
    }

    [Fact]
    [Trait("ac", "AC-9.6")]
    public void Fetch_Mismatch_RemovesEntry_EmitsInformation_NoMarkLoaded_AC96()
    {
        var h = NewHarness();
        var udn = NewUdn();
        var url = Loc();
        var declared = $"uuid:{Guid.NewGuid()}"; // a DIFFERENT root
        h.Parser.Responder = _ => StubDeviceDescriptionParser.Description(declared);
        var removed = new List<string>();
        h.Registry.DeviceRemoved += removed.Add;

        Alive(h.Registry, udn, url);

        h.Registry.Count.Should().Be(0, "mismatched-root entry is removed");
        removed.Should().ContainSingle().Which.Should().Be(udn);
        var urlText = url.ToString();
        h.Diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.DescriptionFetchMismatch)
            .Which.Should().Match<CapturingDiagnosticEmitter.Entry>(e =>
                e.Severity == "Information" &&
                e.Context.DeviceUuid == udn &&
                e.Context.Url == urlText &&
                e.Context.ErrorText == $"declared root: {declared}");
        h.Registry.Loaded.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-9.7")]
    public void Fetch_Cancelled_NoTransition_NoDiagnostic_AC97()
    {
        var h = NewHarness();
        using var adapterCts = new CancellationTokenSource();
        adapterCts.Cancel(); // device CTS is born cancelled
        var udn = NewUdn();

        Alive(h.Registry, udn, Loc(), adapterCts.Token);

        h.Registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Pending, "cancel at the semaphore wait ⇒ no MarkInFlight");
        h.Http.RequestedUrls.Should().BeEmpty("fetch never reached the HTTP call");
        h.Diag.Entries.Should().BeEmpty("cancellation is silent (AC-9.7)");
    }

    [Fact]
    [Trait("ac", "AC-9.fail")]
    public void Fetch_HttpThrows_MarksFailed_EmitsWarning_StaysInRegistry_AC9fail()
    {
        var h = NewHarness();
        var udn = NewUdn();
        var url = Loc();
        h.Http.DescriptionResponder = (_, _) => throw new InvalidOperationException("transport down");

        Alive(h.Registry, udn, url);

        h.Registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Failed);
        entry.FailureReason.Should().Be("transport down");
        h.Registry.Count.Should().Be(1, "failed entries stay in the registry (FR-047)");
        h.Registry.Loaded.Should().BeEmpty("but never appear in the tree");
        var urlText = url.ToString();
        h.Diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.DescriptionFetch)
            .Which.Should().Match<CapturingDiagnosticEmitter.Entry>(e =>
                e.Severity == "Warning" && e.Context.DeviceUuid == udn && e.Context.Url == urlText);
    }

    [Fact]
    [Trait("ac", "AC-9.fail")]
    public void Fetch_ParseThrows_MarksFailed_EmitsWarning_AC9fail()
    {
        var h = NewHarness();
        var udn = NewUdn();
        h.Parser.Responder = _ => throw new InvalidOperationException("malformed xml");

        Alive(h.Registry, udn, Loc());

        h.Registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Failed);
        h.Diag.Entries.Should().ContainSingle(e =>
            e.Category == DiagCategories.DescriptionFetch && e.Severity == "Warning");
    }

    // ─── AC-7.2 per-device byebye drill ────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-7.2")]
    public void Byebye_CancelsOnlyTargetDeviceFetch_AC72()
    {
        var h = NewHarness();
        using var adapterCts = new CancellationTokenSource();
        // Every fetch blocks until ITS device token cancels — so all 5 are genuinely in-flight.
        h.Http.DescriptionResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return Array.Empty<byte>();
        };

        var udns = Enumerable.Range(0, 5).Select(_ => NewUdn()).ToArray();
        for (var i = 0; i < udns.Length; i++)
        {
            Alive(h.Registry, udns[i], Loc(i), adapterCts.Token);
        }

        // All five reached InFlight and are blocked on the HTTP call.
        foreach (var u in udns)
        {
            h.Registry.TryGetEntry(u, out var e).Should().BeTrue();
            e.State.Should().Be(DescriptionFetchState.InFlight);
        }

        h.Registry.OnByebye(udns[2]); // byebye device #3 only

        h.Registry.TryGetEntry(udns[2], out _).Should().BeFalse("byebye removed device #3");
        h.Registry.Count.Should().Be(4);
        for (var i = 0; i < udns.Length; i++)
        {
            if (i == 2)
            {
                continue;
            }

            h.Registry.TryGetEntry(udns[i], out var e).Should().BeTrue();
            e.DeviceToken.IsCancellationRequested.Should().BeFalse(
                "device #3's byebye must NOT cancel other devices (AC-7.2)");
            e.State.Should().Be(DescriptionFetchState.InFlight, "other devices are unaffected");
        }

        h.Diag.Entries.Should().BeEmpty("cancellation is silent — no Warning for the cancelled device");

        adapterCts.Cancel(); // release the 4 still-blocked fetches so no task leaks
    }

    // ─── Amendment A30 regression (d): a non-GUID device reaches DeviceLoaded (the broken path) ──

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void Fetch_NonGuidUdn_ReachesDeviceLoaded_ViaDispatcher()
    {
        var h = NewHarness();
        const string udn = "uuid:linn-ds-akurate-0001"; // a non-RFC-4122 UDN — would have been dropped pre-A30
        // The device-description <UDN> carries the SAME opaque UDN → UdnMatches must accept it.
        h.Parser.Responder = _ => StubDeviceDescriptionParser.Description(udn, "Akurate DS");
        var loaded = new List<RegistryEntry>();
        h.Registry.DeviceLoaded += loaded.Add;

        Alive(h.Registry, udn, Loc()); // synchronous stub ⇒ fetch completes inline

        h.Registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Loaded, "the non-GUID device loads (no Guid.TryParse drop)");
        entry.Description!.FriendlyName.Should().Be("Akurate DS");
        loaded.Should().ContainSingle().Which.Udn.Should().Be(udn, "DeviceLoaded fired for the opaque UDN");
    }

    // ─── NFR-P6 concurrency cap ────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-9.dispatcher")]
    public void Fetch_Concurrency_NeverExceeds8_NFRP6()
    {
        var h = NewHarness();
        using var adapterCts = new CancellationTokenSource();
        h.Http.DescriptionResponder = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return Array.Empty<byte>();
        };

        for (var i = 0; i < 12; i++)
        {
            Alive(h.Registry, NewUdn(), Loc(i), adapterCts.Token);
        }

        h.Http.PeakConcurrency.Should().Be(8, "the SemaphoreSlim(8) caps concurrent fetches (NFR-P6)");

        adapterCts.Cancel(); // release the 8 in-flight + 4 waiting
    }
}
