namespace ohSpy.Core.Tests.Diagnostics;

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ohSpy.Core.Collections;
using ohSpy.Core.Diagnostics;

/// <summary>
/// AC-5 / AC-6 / AC-7 — JSON-lines write format, 2 MB rotation with 8-file retention,
/// startup-failure degrades silently after one warning to ring sink.
/// </summary>
public class DiagnosticFileSinkTests : IDisposable
{
    private readonly string _tempDir;

    public DiagnosticFileSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ohSpy-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            /* tolerate concurrent locks during test teardown */
        }
        GC.SuppressFinalize(this);
    }

    private static DiagnosticEntry MakeEntry(
        DiagSeverity sev = DiagSeverity.Warning,
        string cat = "Http.Timeout",
        string msg = "test",
        DiagnosticContext ctx = default) => new(DateTime.UtcNow, sev, cat, msg, ctx);

    [Fact]
    [Trait("ac", "AC-5")]
    public async Task Push_AppendsJsonLineToTodayFile()
    {
        await using var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, _tempDir);

        var ctx = new DiagnosticContext
        {
            Url = "http://test/",
            Elapsed = TimeSpan.FromMilliseconds(123),
        };
        sink.Push(MakeEntry(DiagSeverity.Warning, "Http.Timeout", "request timed out", ctx));

        await sink.FlushAsync(CancellationToken.None);

        var todayLog = Path.Combine(_tempDir, $"ohSpy-{DateTime.UtcNow:yyyyMMdd}.log");
        File.Exists(todayLog).Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(todayLog);
        lines.Should().HaveCount(1);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;

        root.GetProperty("ts").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        root.GetProperty("sev").GetString().Should().Be("Warning");
        root.GetProperty("cat").GetString().Should().Be("Http.Timeout");
        root.GetProperty("msg").GetString().Should().Be("request timed out");

        var ctxJson = root.GetProperty("ctx");
        ctxJson.GetProperty("Url").GetString().Should().Be("http://test/");
        ctxJson.TryGetProperty("DeviceUuid", out _).Should().BeFalse(
            "null DiagnosticContext fields must be omitted per WhenWritingNull JsonIgnoreCondition");
        ctxJson.TryGetProperty("Elapsed", out var elapsedProp).Should().BeTrue();
        // System.Text.Json serializes TimeSpan as ISO 8601 by default (e.g. "00:00:00.1230000").
        elapsedProp.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("ac", "AC-5")]
    public async Task Push_1000Entries_Yields1000Lines()
    {
        await using var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, _tempDir);

        for (int i = 0; i < 1000; i++)
        {
            sink.Push(MakeEntry(msg: $"msg-{i}"));
        }

        await sink.FlushAsync(CancellationToken.None);

        var todayLog = Path.Combine(_tempDir, $"ohSpy-{DateTime.UtcNow:yyyyMMdd}.log");
        var lines = await File.ReadAllLinesAsync(todayLog);
        lines.Length.Should().Be(1000, "the channel must not drop entries at healthy throughput");
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public async Task Push_RotatesAt2MB()
    {
        await using var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, _tempDir);

        // Channel capacity is 1000 with FullMode=DropOldest, so pushing more than 1000
        // entries faster than the pump drains will silently drop the oldest. Push small
        // batches (well under the 1000 channel cap) with a longer pump-drain delay
        // between batches — that's ~3 MB which comfortably crosses the 2 MB rotation
        // cap with margin, and gives the pump a fair drain window on slower CI.
        var bigMessage = new string('x', 5000);
        for (int batch = 0; batch < 4; batch++)
        {
            for (int i = 0; i < 200; i++)
            {
                sink.Push(MakeEntry(msg: bigMessage));
            }
            await Task.Delay(150);
        }

        await sink.FlushAsync(CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "ohSpy-*.log");
        files.Length.Should().BeGreaterThanOrEqualTo(2, "at >2 MB the sink must rotate to a new file");

        // At least one rotated file must be ≥ 2 MB; the active one may be smaller.
        files.Any(f => new FileInfo(f).Length >= 2L * 1024 * 1024).Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public async Task Push_RetainsAtMost8Files()
    {
        await using var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, _tempDir);

        // Force ~10 rotations. Each entry ~5 KB so ~400 entries/file fills the 2 MB cap.
        // Push in small batches (well under the 1000 channel cap) with generous
        // pump-drain delays to keep DropOldest from biting.
        var bigMessage = new string('y', 5000);
        for (int batch = 0; batch < 20; batch++)
        {
            for (int i = 0; i < 250; i++)
            {
                sink.Push(MakeEntry(msg: bigMessage));
            }
            await Task.Delay(150);
        }

        await sink.FlushAsync(CancellationToken.None);

        var files = Directory.GetFiles(_tempDir, "ohSpy-*.log");
        files.Length.Should().BeLessThanOrEqualTo(8, $"AC-6 caps retained files at 8; observed {files.Length}");
    }

    [Fact]
    [Trait("ac", "AC-7")]
    public async Task Startup_UnwritablePath_EmitsRingSinkWarningAndDisables()
    {
        // Path with NUL byte is guaranteed invalid on Windows AND POSIX — Directory.CreateDirectory throws.
        var badPath = Path.Combine(Path.GetTempPath(), $"bad-\0-{Guid.NewGuid():N}");
        var ringSink = new CapturingRingSink();

        await using var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, badPath);
        sink.SetRingSink(ringSink);

        sink.Push(MakeEntry());

        // Allow the pump task time to hit the startup failure path and emit the warning.
        // The EmitRingSinkUnavailableAsync awaits ring-sink-availability TCS (immediately
        // set) then calls Push. Poll briefly.
        for (int i = 0; i < 50 && ringSink.Pushed.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        ringSink.Pushed.Should().HaveCount(1, "exactly one startup-failure warning must be emitted");
        ringSink.Pushed[0].Severity.Should().Be(DiagSeverity.Warning);
        ringSink.Pushed[0].Category.Should().Be(DiagCategories.DiagnosticsFileSinkUnavailable);
        ringSink.Pushed[0].Context.ErrorText.Should().NotBeNullOrEmpty();

        // Subsequent pushes silently no-op — no exception, no further ring sink pushes.
        sink.Push(MakeEntry());
        sink.Push(MakeEntry());
        await Task.Delay(100);
        ringSink.Pushed.Count.Should().Be(1, "AC-7: subsequent Push calls silently no-op");
    }

    [Fact]
    [Trait("ac", "AC-5")]
    public async Task FlushAsync_DrainsChannelAndClosesFile()
    {
        var sink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, _tempDir);

        for (int i = 0; i < 100; i++)
        {
            sink.Push(MakeEntry(msg: $"line-{i}"));
        }

        await sink.FlushAsync(CancellationToken.None);

        var todayLog = Path.Combine(_tempDir, $"ohSpy-{DateTime.UtcNow:yyyyMMdd}.log");
        var lines = await File.ReadAllLinesAsync(todayLog);
        lines.Length.Should().Be(100, "FlushAsync must drain pending entries");

        // After FlushAsync the channel is completed; subsequent pushes silently no-op
        // (TryWrite returns false but Push doesn't throw).
        sink.Push(MakeEntry(msg: "post-flush"));
        var afterPushLines = await File.ReadAllLinesAsync(todayLog);
        afterPushLines.Length.Should().Be(100, "Push after channel completion must not write");

        await sink.DisposeAsync();
    }

    // ─── Test doubles ──────────────────────────────────────────────────────

    private sealed class CapturingRingSink : IDiagnosticRingSink
    {
        public List<DiagnosticEntry> Pushed { get; } = new();
        public BoundedObservableCollection<DiagnosticRow> Entries { get; } = new(16);
        public void Push(DiagnosticEntry entry) => Pushed.Add(entry);
    }
}
