namespace ohSpy.App;

using System;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Story 6.3 (Option 1 — truly self-contained): the app is published self-contained
        // (csproj: WindowsPackageType=None + WindowsAppSDKSelfContained=true + SelfContained=true), so the
        // Windows App SDK runtime + .NET ship NEXT TO the exe; Application.Start binds the bundled runtime
        // directly — no framework-dependent bootstrapper.
        //
        // NOTE (corrected 2026-06-08): removing Bootstrap.TryInitialize was NOT the actual install fix.
        // The published/installed app crashed at first window because `dotnet publish` DROPPED the WinUI
        // resources (resources.pri / compiled .xbf / Assets) — see the `_CopyWinUIResourcesToPublish`
        // target in ohSpy.App.csproj and Amendment A32. With the resources present, this self-contained
        // (no-bootstrap) startup renders correctly. The old Bootstrap call's 0x80670016 was a separate,
        // dev-build red herring; bootstrap is genuinely unnecessary for a self-contained app.

        // CA1806 suppressed: WinUI 3's Application.Start consumes the App instance via internal
        // machinery; the lambda is the canonical Microsoft-documented startup pattern.
#pragma warning disable CA1806
        Microsoft.UI.Xaml.Application.Start(_ => new App());
#pragma warning restore CA1806
        return 0;
    }
}
