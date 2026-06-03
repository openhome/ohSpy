namespace ohSpy.Core.Shell;

using System.Diagnostics;

/// <summary>
/// Production <see cref="IUriLauncher"/> — opens the URI in the OS default handler via the
/// shell (arch line 2187). <c>UseShellExecute = true</c> is REQUIRED: it routes through the
/// shell so <c>http(s)://</c> URLs open in the registered default browser (without it, .NET
/// tries to exec the URL as a file path and throws). Pure BCL → lives in Core (Pattern 2 /
/// boundary). Not unit-tested directly (it would launch a real browser); covered by the seam
/// contract and the manual smoke (Story 2.8 Task 10.7).
/// </summary>
public sealed class ShellUriLauncher : IUriLauncher
{
    public void Launch(Uri url) =>
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = url.ToString(),
            UseShellExecute = true,
        });
}
