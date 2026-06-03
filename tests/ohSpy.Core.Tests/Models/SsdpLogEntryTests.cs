namespace ohSpy.Core.Tests.Models;

using System.Globalization;
using FluentAssertions;
using ohSpy.Core.Models;

/// <summary>
/// Story 2.7 — <see cref="SsdpLogEntry"/> / <see cref="SsdpLogKind"/> unit tests. Covers AC-2.7.1.
/// </summary>
public sealed class SsdpLogEntryTests
{
    [Fact]
    [Trait("ac", "AC-2.7.1")]
    public void Record_HoldsTimestampKindUuid_AC271()
    {
        var ts = new DateTime(2026, 6, 3, 12, 34, 56, DateTimeKind.Utc);
        var uuid = Guid.NewGuid();

        var entry = new SsdpLogEntry(ts, SsdpLogKind.Alive, uuid);

        entry.TimestampUtc.Should().Be(ts);
        entry.Kind.Should().Be(SsdpLogKind.Alive);
        entry.Uuid.Should().Be(uuid);
        entry.UuidText.Should().Be(uuid.ToString());

        // Records: value equality for identical fields.
        var twin = new SsdpLogEntry(ts, SsdpLogKind.Alive, uuid);
        entry.Should().Be(twin);
    }

    [Theory]
    [Trait("ac", "AC-2.7.1")]
    [InlineData(SsdpLogKind.Alive, "ALIVE")]
    [InlineData(SsdpLogKind.Byebye, "BYEBYE")]
    public void KindToken_MapsAliveAndByebye_AC271(SsdpLogKind kind, string expected)
    {
        var entry = new SsdpLogEntry(DateTime.UtcNow, kind, Guid.NewGuid());

        entry.KindToken.Should().Be(expected);
    }

    [Fact]
    [Trait("ac", "AC-2.7.1")]
    public void TimestampDisplay_FormatsLocalHmsMillis_AC271()
    {
        var ts = new DateTime(2026, 6, 3, 12, 34, 56, 789, DateTimeKind.Utc);
        var entry = new SsdpLogEntry(ts, SsdpLogKind.Alive, Guid.NewGuid());

        // Local-time conversion is machine-TZ-relative, so compare against the same
        // expression rather than a hard-coded string. Asserts the format + invariant culture.
        var expected = ts.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        entry.TimestampDisplay.Should().Be(expected);
    }
}
