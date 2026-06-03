namespace ohSpy.Core.Shell;

/// <summary>
/// One-method seam over the OS "open this URI in its default handler" shell call
/// (FR-019 / FR-020). The single production impl (<see cref="ShellUriLauncher"/>) calls
/// <c>Process.Start(UseShellExecute = true)</c>; tests inject a fake so the whitelist +
/// warn-on-failure logic (Gap-3) is verifiable without spawning a browser.
/// </summary>
public interface IUriLauncher
{
    /// <summary>
    /// Hand the URI to the OS shell. Throws on any launch failure (no default browser,
    /// blocked scheme handler, etc.) — the caller is responsible for catching and emitting
    /// the FR-019 Warning diagnostic.
    /// </summary>
    void Launch(Uri url);
}
