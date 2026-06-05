namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.ViewModels;

/// <summary>
/// Capturing <see cref="IDiagnosticsLauncher"/>: counts <see cref="Open"/> calls so tests can assert
/// ShellViewModel.OpenDiagnosticsCommand crosses the Core/App seam (Story 5.1). Mirror of
/// <see cref="FakeSubscriptionPopupLauncher"/>.
/// </summary>
internal sealed class FakeDiagnosticsLauncher : IDiagnosticsLauncher
{
    public int OpenCount { get; private set; }

    public void Open() => OpenCount++;
}
