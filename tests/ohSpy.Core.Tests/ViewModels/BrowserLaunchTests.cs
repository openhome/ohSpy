namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.8 — <see cref="BrowserLaunch.OpenInDefaultBrowser"/> unit tests. The shared
/// shell-open helper: Gap-3 http/https whitelist + warn-on-refusal + warn-on-launch-failure
/// (AC-2.8.2 / AC-2.8.3). Exercised via the <see cref="FakeUriLauncher"/> seam so no real
/// browser is spawned.
/// </summary>
public sealed class BrowserLaunchTests
{
    private const string Udn = "uuid:22222222-2222-2222-2222-222222222222";

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void Http_LaunchesUrl_AC282()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var url = new Uri("http://192.168.1.100:49152/desc.xml");

        var result = BrowserLaunch.OpenInDefaultBrowser(url, launcher, diag, Udn);

        result.Should().BeTrue();
        launcher.Launched.Should().ContainSingle().Which.Should().Be(url);
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void Https_LaunchesUrl_AC282()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var url = new Uri("https://device.local/desc.xml");

        var result = BrowserLaunch.OpenInDefaultBrowser(url, launcher, diag, Udn);

        result.Should().BeTrue();
        launcher.Launched.Should().ContainSingle().Which.Should().Be(url);
    }

    [Theory]
    [Trait("ac", "AC-2.8.3")]
    [InlineData("file:///c:/secrets.xml")]
    [InlineData("ftp://host/file.xml")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:a@b.com")]
    public void NonHttpScheme_Refused_NoLaunch_Warns_AC283(string raw)
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var url = new Uri(raw);

        var result = BrowserLaunch.OpenInDefaultBrowser(url, launcher, diag, Udn);

        result.Should().BeFalse();
        launcher.Launched.Should().BeEmpty("a non-http(s) scheme must never be shell-opened");
        var warning = diag.Entries.Should().ContainSingle().Which;
        warning.Severity.Should().Be("Warning");
        warning.Category.Should().Be(DiagCategories.ShellExecute);
        warning.Context.Url.Should().Be(url.ToString());
    }

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void LaunchThrows_Warns_NoCrash_AC282()
    {
        var launcher = new FakeUriLauncher { ThrowOnLaunch = new InvalidOperationException("no browser") };
        var diag = new CapturingDiagnosticEmitter();
        var url = new Uri("http://host/desc.xml");

        var act = () => BrowserLaunch.OpenInDefaultBrowser(url, launcher, diag, Udn);

        act.Should().NotThrow("a launch failure must warn-not-crash (FR-019)");
        var warning = diag.Entries.Should().ContainSingle().Which;
        warning.Severity.Should().Be("Warning");
        warning.Category.Should().Be(DiagCategories.ShellExecute);
        warning.Context.ErrorText.Should().Be("no browser");
        warning.Context.Url.Should().Be(url.ToString());
    }

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void DeviceUuid_FlowsToDiagnosticContext_AC282()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();

        BrowserLaunch.OpenInDefaultBrowser(new Uri("ftp://host/x"), launcher, diag, Udn);

        diag.Entries.Should().ContainSingle()
            .Which.Context.DeviceUuid.Should().Be(Udn);
    }
}
