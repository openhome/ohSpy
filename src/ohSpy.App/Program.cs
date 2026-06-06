namespace ohSpy.App;

using System;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Story 6.3 (Option 1 — truly self-contained): this app is published self-contained
        // (csproj: WindowsPackageType=None + WindowsAppSDKSelfContained=true + SelfContained=true), so the
        // Windows App SDK runtime + the .NET runtime ship NEXT TO the exe and are loaded directly via the
        // app's own .deps.json / runtimeconfig. There is NO framework-dependent bootstrapper:
        // Application.Start binds the bundled WinAppSDK runtime itself.
        //
        // The previous Bootstrap.TryInitialize(2.1.3 minVersion) call required an INSTALLED Windows App
        // Runtime ≥ 2.1.3 — the opposite of self-contained — and died on a clean box with the native
        // MessageBox "Windows App Runtime initialisation failed (0x80670016)". Removing it makes the
        // self-contained config real (architecture amendment A32; deferred-work resolved in Story 6.3).

        // CA1806 suppressed: WinUI 3's Application.Start consumes the App instance via internal
        // machinery; the lambda is the canonical Microsoft-documented startup pattern.
#pragma warning disable CA1806
        Microsoft.UI.Xaml.Application.Start(_ => new App());
#pragma warning restore CA1806
        return 0;
    }
}
