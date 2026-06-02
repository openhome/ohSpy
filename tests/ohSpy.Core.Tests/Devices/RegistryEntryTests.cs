namespace ohSpy.Core.Tests.Devices;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.3 — <see cref="RegistryEntry"/> state machine (Decision 9). Covers the
/// AC-9.1 legal/illegal transition matrix, the AC-9.2 Description-iff-Loaded invariant,
/// AC-9.4 metadata refresh (no transition), and the AC-7.2 device-CTS linkage. Every
/// AC-traceable test carries <c>[Trait("ac", "AC-9.&lt;n&gt;")]</c>.
/// </summary>
public sealed class RegistryEntryTests
{
    private static RegistryEntry NewEntry(CancellationToken adapterToken = default) =>
        new(Guid.NewGuid(), new Uri("http://192.0.2.10:49152/desc.xml"), DateTime.UtcNow, adapterToken);

    [Fact]
    [Trait("ac", "AC-9.0")]
    public void State_Enum_HasFourValues_AC90()
    {
        Enum.GetValues<DescriptionFetchState>().Should().BeEquivalentTo(new[]
        {
            DescriptionFetchState.Pending,
            DescriptionFetchState.InFlight,
            DescriptionFetchState.Loaded,
            DescriptionFetchState.Failed,
        });
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void NewEntry_StartsPending_AC91()
    {
        NewEntry().State.Should().Be(DescriptionFetchState.Pending);
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void PendingToInFlightToLoaded_Succeeds_AC91()
    {
        var entry = NewEntry();

        entry.MarkInFlight();
        entry.State.Should().Be(DescriptionFetchState.InFlight);

        entry.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{entry.Uuid}"));
        entry.State.Should().Be(DescriptionFetchState.Loaded);
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void PendingToFailed_Succeeds_AC91()
    {
        var entry = NewEntry();

        entry.MarkFailed("never started");

        entry.State.Should().Be(DescriptionFetchState.Failed);
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void InFlightToFailed_Succeeds_AC91()
    {
        var entry = NewEntry();
        entry.MarkInFlight();

        entry.MarkFailed("fetch error");

        entry.State.Should().Be(DescriptionFetchState.Failed);
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void MarkLoaded_FromPending_Throws_AC91()
    {
        var entry = NewEntry();
        var act = () => entry.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{entry.Uuid}"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void MarkInFlight_Twice_Throws_AC91()
    {
        var entry = NewEntry();
        entry.MarkInFlight();
        var act = () => entry.MarkInFlight();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void LoadedIsTerminal_AnyTransitionThrows_AC91()
    {
        var entry = NewEntry();
        entry.MarkInFlight();
        entry.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{entry.Uuid}"));

        entry.Invoking(e => e.MarkInFlight()).Should().Throw<InvalidOperationException>();
        entry.Invoking(e => e.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{e.Uuid}")))
            .Should().Throw<InvalidOperationException>();
        entry.Invoking(e => e.MarkFailed("x")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("ac", "AC-9.1")]
    public void FailedIsTerminal_AnyTransitionThrows_AC91()
    {
        var entry = NewEntry();
        entry.MarkFailed("dead");

        entry.Invoking(e => e.MarkInFlight()).Should().Throw<InvalidOperationException>();
        entry.Invoking(e => e.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{e.Uuid}")))
            .Should().Throw<InvalidOperationException>();
        entry.Invoking(e => e.MarkFailed("again")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("ac", "AC-9.2")]
    public void Description_NonNull_IffLoaded_AC92()
    {
        var entry = NewEntry();
        entry.Description.Should().BeNull("Pending has no description");

        entry.MarkInFlight();
        entry.Description.Should().BeNull("InFlight has no description");

        var desc = StubDeviceDescriptionParser.Description($"uuid:{entry.Uuid}");
        entry.MarkLoaded(desc);
        entry.Description.Should().BeSameAs(desc);
    }

    [Fact]
    [Trait("ac", "AC-9.2")]
    public void FailureReason_NonNull_IffFailed_AC92()
    {
        var entry = NewEntry();
        entry.FailureReason.Should().BeNull();

        entry.MarkFailed("boom");
        entry.FailureReason.Should().Be("boom");
    }

    [Fact]
    [Trait("ac", "AC-9.4")]
    public void RefreshSsdpMetadata_DoesNotTransition_BumpsLiveness_AC94()
    {
        var entry = NewEntry();
        var stateBefore = entry.State;
        var t = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

        entry.RefreshSsdpMetadata(t, "Linn/1.0", TimeSpan.FromSeconds(1800), "7", "3");

        entry.State.Should().Be(stateBefore, "refresh must not transition the state machine");
        entry.AliveCount.Should().Be(1);
        entry.LastSeenUtc.Should().Be(t);
        entry.Server.Should().Be("Linn/1.0");
        entry.CacheControlMaxAge.Should().Be(TimeSpan.FromSeconds(1800));
        entry.BootId.Should().Be("7");
        entry.ConfigId.Should().Be("3");

        entry.RefreshSsdpMetadata(t, "Linn/1.0", null, null, null);
        entry.AliveCount.Should().Be(2, "each alive increments the count");
    }

    [Fact]
    [Trait("ac", "AC-7.2")]
    public void DeviceCts_LinkedToAdapterToken_AC72()
    {
        using var adapterCts = new CancellationTokenSource();
        var entry = NewEntry(adapterCts.Token);

        entry.DeviceToken.IsCancellationRequested.Should().BeFalse();
        adapterCts.Cancel();
        entry.DeviceToken.IsCancellationRequested.Should().BeTrue(
            "the device CTS is linked to the adapter token (Decision 7)");
    }
}
