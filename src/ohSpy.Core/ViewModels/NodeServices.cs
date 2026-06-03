namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;
using ohSpy.Core.Shell;
using ohSpy.Core.Threading;

/// <summary>
/// Immutable bundle of the Core services a tree-node ViewModel needs to lazily fetch and
/// parse an SCPD on expand (Story 2.6). Constructed once in the composition root and
/// threaded DeviceTree → DeviceNode → ServiceNode so node VMs (created via `new`, not DI)
/// can reach the HTTP client, parser, dispatcher, and diagnostic emitter without each
/// taking four constructor arguments.
/// </summary>
public sealed record NodeServices(
    IUpnpHttpClient Http,
    IScpdParser ScpdParser,
    IUiDispatcher Ui,
    IDiagnosticEmitter Diag,
    IUriLauncher Launcher,           // Story 2.8 — context-menu shell-open seam
    IPropertiesLauncher PropertiesLauncher,  // Story 2.9 — open the Properties window
    IInvocationPopupLauncher InvocationPopupLauncher); // Story 3.2 — open the invocation popup
