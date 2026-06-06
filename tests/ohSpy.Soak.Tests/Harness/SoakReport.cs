namespace ohSpy.Soak.Tests.Harness;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Story 6.2 Task 6 (AC-6.2.10 / AC-6.2.11) — captures a soak run's outcome and writes a Markdown
/// report under <c>docs/soak-reports/&lt;yyyy-MM-dd-HHmm&gt;-&lt;duration&gt;.md</c>. The report records
/// environment, farm composition, the memory-sample table, exception count, max dispatch latency, the
/// on-disk-log rollover result, and the ⭐#4 headless caveat + the 6.3 cross-reference.
/// </summary>
internal sealed class SoakReport
{
    public required string Title { get; init; }                 // "30min" | "8hr"
    public required DateTime StartUtc { get; init; }
    public required TimeSpan ConfiguredDuration { get; init; }
    public required TimeSpan ActualDuration { get; init; }
    public required string FarmComposition { get; init; }
    public required int SubscriptionPopups { get; init; }
    public required int AdvertsPerSecond { get; init; }
    public required IReadOnlyList<MemorySampler.Sample> MemorySamples { get; init; }
    public required int UnhandledExceptionCount { get; init; }
    public required TimeSpan MaxDispatchGap { get; init; }
    public required int StallCount { get; init; }
    public required bool PopupsClosable { get; init; }
    public required bool DiagnosticsResponsive { get; init; }
    public required RolloverResult Rollover { get; init; }
    public required CapsSnapshot Caps { get; init; }
    public IReadOnlyList<string> Anomalies { get; init; } = Array.Empty<string>();

    public readonly record struct RolloverResult(int FileCount, long LargestFileBytes, bool Applied);

    public readonly record struct CapsSnapshot(
        int SsdpLogCount, int SsdpLogCap,
        int MaxEventListCount, int EventListCap,
        int RingCount, int RingCap,
        long OnDiskBytes, long OnDiskCapBytes, int OnDiskFiles, int OnDiskFileCap);

    /// <summary>Render the report to Markdown and write it to docs/soak-reports/. Returns the path.</summary>
    public string Write()
    {
        var reportsDir = Path.Combine(RepoRoot(), "docs", "soak-reports");
        Directory.CreateDirectory(reportsDir);
        var fileName = $"{StartUtc:yyyy-MM-dd-HHmm}-{Title}.md";
        var path = Path.Combine(reportsDir, fileName);
        File.WriteAllText(path, Render(), Encoding.UTF8);
        return path;
    }

    private string Render()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# ohSpy Soak Report — {Title}\n\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- Date / start: {StartUtc:yyyy-MM-dd HH:mm} UTC   Duration (configured): {ConfiguredDuration}   Duration (actual): {ActualDuration}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- Build / commit: {BuildSha()}   .NET: {RuntimeInformation.FrameworkDescription}   Machine: {MachineLine()}\n");
        sb.Append("- Mode: HEADLESS Core soak (drives the Core VM + service stack against an in-process FakeUpnpDevice farm; NOT the WinUI app).\n");
        sb.Append("  Full-app resident memory is verified separately by Story 6.3 (interactive SC-013). The < 200 MB figure below is a generous HEADLESS ceiling.\n\n");

        sb.Append("## Farm composition\n\n");
        sb.Append("| Devices | Subscription popups | SSDP adv/s |\n|---|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| {FarmComposition} | {SubscriptionPopups} | {AdvertsPerSecond} |\n\n");

        sb.Append("## Memory samples\n\n");
        sb.Append("(plateau after warm-up = bounded / no leak)\n\n");
        sb.Append("| t | WorkingSet64 (MB) | PrivateMemorySize64 (MB) |\n|---|---|---|\n");
        foreach (var s in MemorySamples)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {s.At:hh\\:mm\\:ss} | {Mb(s.WorkingSetBytes)} | {Mb(s.PrivateBytes)} |\n");
        }
        sb.Append('\n');

        sb.Append("## Bounded-collection caps at end\n\n");
        sb.Append("| Collection | Cap | Observed |\n|---|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| SSDP log | {Caps.SsdpLogCap} | {Caps.SsdpLogCount} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Subscription event list (max) | {Caps.EventListCap} | {Caps.MaxEventListCount} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Diagnostic ring | {Caps.RingCap} | {Caps.RingCount} |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| On-disk log | ≤ {Mb(Caps.OnDiskCapBytes)} MB / ≤ {Caps.OnDiskFileCap} files | {Mb(Caps.OnDiskBytes)} MB / {Caps.OnDiskFiles} files |\n\n");

        sb.Append("## Assertions\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"- Unhandled exceptions: {UnhandledExceptionCount}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- UI-thread stalls > 1 s: {StallCount}   (max dispatch gap: {MaxDispatchGap.TotalMilliseconds:F0} ms)\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- Popups closable: {(PopupsClosable ? "yes" : "no")}   DiagnosticsViewModel responsive at end: {(DiagnosticsResponsive ? "yes" : "no")}\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"- On-disk rollover: {Rollover.FileCount} files, max {Mb(Rollover.LargestFileBytes)} MB, rollover applied: {(Rollover.Applied ? "yes" : "n/a (compressed smoke did not exceed 2 MB)")}\n\n");

        sb.Append("## Anomalies / notes\n\n");
        if (Anomalies.Count == 0)
        {
            sb.Append("none. (Soak flakes are investigated as real defects, not retried-until-green.)\n");
        }
        else
        {
            foreach (var a in Anomalies)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- {a}\n");
            }
        }
        return sb.ToString();
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

    private static string MachineLine() =>
        $"{Environment.ProcessorCount} CPU / {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}";

    private static string BuildSha()
    {
        // Best-effort: read .git/HEAD → ref → sha. Falls back to "unknown" outside a checkout.
        try
        {
            var head = Path.Combine(RepoRoot(), ".git", "HEAD");
            if (!File.Exists(head))
            {
                return "unknown";
            }
            var content = File.ReadAllText(head).Trim();
            if (content.StartsWith("ref:", StringComparison.Ordinal))
            {
                var refPath = Path.Combine(RepoRoot(), ".git", content[5..].Trim());
                return File.Exists(refPath) ? File.ReadAllText(refPath).Trim()[..Math.Min(12, 40)] : content;
            }
            return content.Length >= 12 ? content[..12] : content;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "unknown";
        }
    }

    // Walk up from the test output dir to the repo root (the dir containing ohSpy.sln).
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ohSpy.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        // Fallback: current directory.
        return Directory.GetCurrentDirectory();
    }
}
