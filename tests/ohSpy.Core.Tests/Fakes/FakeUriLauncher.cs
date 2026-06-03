namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Shell;

/// <summary>
/// Records every <see cref="Launch"/> call so tests can assert the URL that was shell-opened;
/// set <see cref="ThrowOnLaunch"/> to simulate "no default browser" (FR-019).
/// </summary>
internal sealed class FakeUriLauncher : IUriLauncher
{
    public List<Uri> Launched { get; } = new();
    public Exception? ThrowOnLaunch { get; set; }

    public void Launch(Uri url)
    {
        Launched.Add(url);
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
    }
}
