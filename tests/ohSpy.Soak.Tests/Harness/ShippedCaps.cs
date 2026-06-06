namespace ohSpy.Soak.Tests.Harness;

using System.Reflection;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 6.2 (⭐#3 / AC-6.2.8) — reads the SHIPPED bounded-collection cap CONSTANTS by reflection
/// rather than retyping the epic's literals, so a future cap change can't silently desync the soak
/// gate. Each value is read from the private <c>const</c> on the production type that owns it; if a
/// constant is renamed/removed the reflection throws and the soak fails loudly (the intended guard).
/// <para>Verified shipped (against main): SSDP log 10,000; subscription event list 5,000; diagnostic
/// ring 5,000; on-disk 2 MB/file × 8 files = 16 MB.</para>
/// </summary>
internal static class ShippedCaps
{
    /// <summary>SSDP message-log cap — <c>SsdpLogViewModel.Capacity</c> (FR-016 + D6).</summary>
    public static int SsdpLogCapacity => ReadIntConst(typeof(SsdpLogViewModel), "Capacity");

    /// <summary>Per-popup subscription event-list cap (FR-033 + D6). Read from a live VM instance's
    /// <c>BoundedObservableCollection.Capacity</c> so no literal is retyped.</summary>
    public static int SubscriptionEventListCapacity(SubscriptionPopupViewModel popup) =>
        popup.Events.Capacity;

    /// <summary>Per-popup subscription event-list cap read from the shipped
    /// <c>SubscriptionPopupViewModel.EventListCapacity</c> const — available WITHOUT a live popup
    /// instance (so the snapshot never falls back to a retyped literal).</summary>
    public static int SubscriptionEventListCapacityConst =>
        ReadIntConst(typeof(SubscriptionPopupViewModel), "EventListCapacity");

    /// <summary>Diagnostic ring cap — <c>DiagnosticRingSink.Capacity</c> (FR-041). Read from a live
    /// ring sink's <c>Entries.Capacity</c> so no literal is retyped.</summary>
    public static int DiagnosticRingCapacity(DiagnosticRingSink ring) => ring.Entries.Capacity;

    /// <summary>On-disk per-file cap in bytes — <c>DiagnosticFileSink.MaxFileBytes</c> (AC-8.5).</summary>
    public static long DiagnosticFileMaxBytes => ReadLongConst(typeof(DiagnosticFileSink), "MaxFileBytes");

    /// <summary>On-disk retained-file cap — <c>DiagnosticFileSink.MaxRetainedFiles</c> (AC-8.5).</summary>
    public static int DiagnosticFileMaxRetained => ReadIntConst(typeof(DiagnosticFileSink), "MaxRetainedFiles");

    /// <summary>Total on-disk cap (≤ 16 MB) — derived from the two shipped constants, not a literal.</summary>
    public static long DiagnosticFileTotalCapBytes => DiagnosticFileMaxBytes * DiagnosticFileMaxRetained;

    private static int ReadIntConst(Type type, string name) => (int)ReadConst(type, name);

    private static long ReadLongConst(Type type, string name) => (long)ReadConst(type, name);

    private static object ReadConst(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Shipped cap constant {type.Name}.{name} not found — the soak gate is out of sync with production. " +
                "Investigate (a soak flake is a real defect): the cap was renamed/removed.");
        return field.GetRawConstantValue()
            ?? throw new InvalidOperationException($"{type.Name}.{name} has no constant value.");
    }
}
