namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Diagnostics;
using ohSpy.Core.Shell;

/// <summary>
/// Shared shell-open path for the Story 2.8 context-menu "Fetch XML" commands (FR-019 /
/// FR-020). Enforces the Gap-3 scheme whitelist, delegates the actual launch to the injected
/// <see cref="IUriLauncher"/>, and emits a single Warning diagnostic on either a refused
/// scheme or a launch failure — never throws, never crashes the app (AC-2.8.2 / AC-2.8.3).
/// UI-thread, synchronous, fire-and-forget (AC-2.8.6).
/// </summary>
internal static class BrowserLaunch
{
    /// <summary>
    /// Open <paramref name="url"/> in the default browser if (and only if) its scheme is
    /// http/https. Returns true if the launch was attempted, false if it was refused or
    /// failed (both paths having emitted a Warning).
    /// </summary>
    public static bool OpenInDefaultBrowser(
        Uri url, IUriLauncher launcher, IDiagnosticEmitter diag, string deviceUdn)
    {
        // Gap-3 whitelist: UPnP LOCATION / SCPDURL are http(s) per UDA 1.0. Anything else
        // (file:, javascript:, custom schemes) is refused defensively — never shell-opened.
        if (!IsHttpOrHttps(url))
        {
            diag.Warning(DiagCategories.ShellExecute, "Refused to open non-http(s) URL",
                new DiagnosticContext { DeviceUuid = deviceUdn, Url = url.ToString() });
            return false;
        }

        try
        {
            launcher.Launch(url);
            return true;
        }
#pragma warning disable CA1031 // FR-019: ANY launch failure (Win32Exception "no default
        catch (Exception ex) // browser", blocked handler, etc.) must warn-not-crash.
#pragma warning restore CA1031
        {
            diag.Warning(DiagCategories.ShellExecute, "Failed to open URL in default browser",
                new DiagnosticContext
                {
                    DeviceUuid = deviceUdn, Url = url.ToString(), ErrorText = ex.Message,
                });
            return false;
        }
    }

    private static bool IsHttpOrHttps(Uri url) =>
        url.IsAbsoluteUri &&
        (url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
