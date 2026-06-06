namespace ohSpy.Soak.Tests.Harness;

using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;
using ohSpy.Core.Shell;
using ohSpy.Core.ViewModels;

// Story 6.2 — minimal launcher stubs to satisfy the NodeServices bundle. The soak constructs popup
// VMs DIRECTLY (it never opens WinUI windows — CoreAppBoundaryTests forbid it), so these App-seam
// launchers are never actually invoked by the session script; they exist only so the real Core VM
// graph (DeviceTree → DeviceNode → ServiceNode → ActionNode) can be assembled.

internal sealed class NoOpUriLauncher : IUriLauncher
{
    public void Launch(Uri url) { /* soak never shell-opens */ }
}

internal sealed class NoOpPropertiesLauncher : IPropertiesLauncher
{
    public void OpenProperties(RegistryEntry entry) { /* soak constructs PropertiesViewModel directly */ }
}

internal sealed class NoOpInvocationPopupLauncher : IInvocationPopupLauncher
{
    public void Open(ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry) { }
}

internal sealed class NoOpSubscriptionPopupLauncher : ISubscriptionPopupLauncher
{
    public void Open(ServiceDescription service, RegistryEntry parentEntry) { }
}

internal sealed class NoOpDiagnosticsLauncher : IDiagnosticsLauncher
{
    public void Open() { /* soak constructs DiagnosticsViewModel directly */ }
}

/// <summary>Always-null identity lookup (the ring sink falls back to the UDN string). Mirrors the
/// shipped <c>NullIdentityLookup</c>; the soak does not assert friendly-name resolution.</summary>
internal sealed class SoakIdentityLookup : IDiagnosticIdentityLookup
{
    public string? TryGetFriendlyName(string udn) => null;
}
