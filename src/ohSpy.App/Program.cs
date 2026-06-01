using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace ohSpy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Bind to the Windows App Runtime self-contained-published alongside this exe.
        // MUST run before any Microsoft.UI.Xaml type is touched.
        // API surface (WindowsAppSDK 2.x):
        //   bool Bootstrap.TryInitialize(uint majorMinorVersion, string versionTag,
        //                                PackageVersion minVersion, InitializeOptions options,
        //                                out int hr)
        // Returns true on success; false + non-zero hr on failure.
        var minVersion = new PackageVersion(major: 2, minor: 1, build: 3, revision: 0);
        var ok = Bootstrap.TryInitialize(
            majorMinorVersion: 0x00020001,            // WindowsAppSDK 2.1.x
            versionTag: "",
            minVersion: minVersion,
            options: Bootstrap.InitializeOptions.None,
            out var hr);

        if (!ok)
        {
            // Bootstrap failed — runtime missing or mismatched.
            // No WinUI available yet; no diagnostic sink yet. Native message box + exit is terminal.
            _ = MessageBoxW(
                IntPtr.Zero,
                $"Windows App Runtime initialisation failed (0x{hr:X8}).\n\n" +
                "Reinstall ohSpy. If the problem persists, contact the ohSpy maintainers.",
                "ohSpy",
                MB_OK | MB_ICONERROR);
            return hr;
        }

        try
        {
            // CA1806 suppressed: WinUI 3's Application.Start consumes the App instance via internal
            // machinery; the lambda is the canonical Microsoft-documented startup pattern.
#pragma warning disable CA1806
            Microsoft.UI.Xaml.Application.Start(_ => new App());
#pragma warning restore CA1806
        }
        finally
        {
            Bootstrap.Shutdown();
        }
        return 0;
    }

    private const uint MB_OK = 0x0u;
    private const uint MB_ICONERROR = 0x10u;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
