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

    private static void Alive(DeviceRegistry r, Guid uuid, Uri url, CancellationToken adapterToken = default) =>
        r.OnAlive(uuid, url, DateTime.UtcNow, "Linn/1.0", TimeSpan.FromSeconds(1800), "1", "1", adapterToken);

    // ─── UdnMatches (AC-9.6 normalisation) ─────────────────────────────────────

    [Theory]
    [Trait("ac", "AC-9.6")]
    [InlineData("uuid:2fac1234-31f8-11b4-a222-08002b34c003", "2fac1234-31f8-11b4-a222-08002b34c003", true)]
    [InlineData("UUID:2FAC1234-31F8-11B4-A222-08002B34C003", "2fac1234-31f8-11b4-a222-08002b34c003", true)]
    [InlineData("2fac1234-31f8-11b4-a222-08002b34c003", "2fac1234-31f8-11b4-a222-08002b34c003", true)]
    [InlineData("uuid:00000000-0000-0000-0000-000000000001", "2fac1234-31f8-11b4-a222-08002b34c003", false)]
    [InlineData("not-a-uuid", "2fac1234-31f8-11b4-a222-08002b34c003", false)]
    public void UdnMatches_NormalisesPrefixAndCasing_AC96(string udn, string uuid, bool expected)
    {
        EagerDescriptionDispatcher.UdnMatches(udn, Guid.Parse(uuid)).Should().Be(expected);
    }

    // ─── Canonical flows ───────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-9.flow")]
    public void Fetch_Happy_MarksLoaded_RaisesDeviceLoaded()
    {
        var h = NewHarness();
        var uuid = Guid.NewGuid();
        h.Parser.Responder = _ => StubDeviceDescriptionParser.Description($"uuid:{uuid}", "Living Room");
        var loaded = new List<RegistryEntry>();
        h.Registry.DeviceLoaded += loaded.Add;

        Alive(h.Registry, uuid, Loc()); // synchronous stub ⇒ fetch completes inline

        h.Registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Loaded);
        entry.Description!.FriendlyName.Should().Be("Living Room");
        h.Registry.Loaded.Should().ContainSingle().Which.Uuid.Should().Be(uuid);
        loaded.Should().ContainSingle().Which.Should().BeSameAs(entry);
        h.Http.RequestedUrls.Should().ContainSingle().Which.Should().Be(Loc());
    }

    [Fact]
    [Trait("ac", "AC-9.6")]
    public void Fetch_Mismatch_RemovesEntry_EmitsInformation_NoMarkLoaded_AC96()
    {
        var h = NewHarness();
        var uuid = Guid.NewGuid();
        var url = Loc();
        var declared = $"uuid:{Guid.NewGuid()}"; // a DIFFERENT root
        h.Parser.Responder = _ => StubDeviceDescriptionParser.Description(declared);
        var removed = new List<Guid>();
        h.Registry.DeviceRemoved += removed.Add;

        Alive(h.Registry, uuid, url);

        h.Registry.Count.Should().Be(0, "mismatched-root entry is removed");
        removed.Should().ContainSingle().Which.Should().Be(uuid);
        var urlText = url.ToString();
        h.Diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.DescriptionFetchMismatch)
            .Which.Should().Match<CapturingDiagnosticEmitter.Entry>(e =>
                e.Severity == "Information" &&
                e.Context.DeviceUuid == uuid &&
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
        var uuid = Guid.NewGuid();

        Alive(h.Registry, uuid, Loc(), adapterCts.Token);

        h.Registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Pending, "cancel at the semaphore wait ⇒ no MarkInFlight");
        h.Http.RequestedUrls.Should().BeEmpty("fetch never reached the HTTP call");
        h.Diag.Entries.Should().BeEmpty("cancellation is silent (AC-9.7)");
    }

    [Fact]
    [Trait("ac", "AC-9.fail")]
    public void Fetch_HttpThrows_MarksFailed_EmitsWarning_StaysInRegistry_AC9fail()
    {
        var h = NewHarness();
        var uuid = Guid.NewGuid();
        var url = Loc();
        h.Http.DescriptionResponder = (_, _) => throw new InvalidOperationException("transport down");

        Alive(h.Registry, uuid, url);

        h.Registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Failed);
        entry.FailureReason.Should().Be("transport down");
        h.Registry.Count.Should().Be(1, "failed entries stay in the registry (FR-047)");
        h.Registry.Loaded.Should().BeEmpty("but never appear in the tree");
        var urlText = url.ToString();
        h.Diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.DescriptionFetch)
            .Which.Should().Match<CapturingDiagnosticEmitter.Entry>(e =>
                e.Severity == "Warning" && e.Context.DeviceUuid == uuid && e.Context.Url == urlText);
    }

    [Fact]
    [Trait("ac", "AC-9.fail")]
    public void Fetch_ParseThrows_MarksFailed_EmitsWarning_AC9fail()
    {
        var h = NewHarness();
        var uuid = Guid.NewGuid();
        h.Parser.Responder = _ => throw new InvalidOperationException("malformed xml");

        Alive(h.Registry, uuid, Loc());

        h.Registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
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

        var uuids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        for (var i = 0; i < uuids.Length; i++)
        {
            Alive(h.Registry, uuids[i], Loc(i), adapterCts.Token);
        }

        // All five reached InFlight and are blocked on the HTTP call.
        foreach (var u in uuids)
        {
            h.Registry.TryGetEntry(u, out var e).Should().BeTrue();
            e.State.Should().Be(DescriptionFetchState.InFlight);
        }

        h.Registry.OnByebye(uuids[2]); // byebye device #3 only

        h.Registry.TryGetEntry(uuids[2], out _).Should().BeFalse("byebye removed device #3");
        h.Registry.Count.Should().Be(4);
        for (var i = 0; i < uuids.Length; i++)
        {
            if (i == 2)
            {
                continue;
            }

            h.Registry.TryGetEntry(uuids[i], out var e).Should().BeTrue();
            e.DeviceToken.IsCancellationRequested.Should().BeFalse(
                "device #3's byebye must NOT cancel other devices (AC-7.2)");
            e.State.Should().Be(DescriptionFetchState.InFlight, "other devices are unaffected");
        }

        h.Diag.Entries.Should().BeEmpty("cancellation is silent — no Warning for the cancelled device");

        adapterCts.Cancel(); // release the 4 still-blocked fetches so no task leaks
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
            Alive(h.Registry, Guid.NewGuid(), Loc(i), adapterCts.Token);
        }

        h.Http.PeakConcurrency.Should().Be(8, "the SemaphoreSlim(8) caps concurrent fetches (NFR-P6)");

        adapterCts.Cancel(); // release the 8 in-flight + 4 waiting
    }
}
