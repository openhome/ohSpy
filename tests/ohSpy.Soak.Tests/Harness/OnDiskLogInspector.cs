namespace ohSpy.Soak.Tests.Harness;

/// <summary>
/// Story 6.2 (⭐#6 / AC-6.2.11) — inspects the temp diagnostics dir after the run to verify size-based
/// rollover end-to-end against the shipped <c>DiagnosticFileSink</c>: file count, total bytes, and the
/// largest file. The sink writes <c>ohSpy-yyyyMMdd.log</c> + sequenced siblings
/// <c>ohSpy-yyyyMMdd-NNN.log</c> and prunes to ≤ 8.
/// </summary>
internal static class OnDiskLogInspector
{
    public readonly record struct Result(int FileCount, long TotalBytes, long LargestFileBytes);

    public static Result Inspect(string diagnosticsDir)
    {
        if (!Directory.Exists(diagnosticsDir))
        {
            return new Result(0, 0, 0);
        }
        var files = Directory.GetFiles(diagnosticsDir, "ohSpy-*.log");
        long total = 0;
        long largest = 0;
        foreach (var file in files)
        {
            var len = new FileInfo(file).Length;
            total += len;
            if (len > largest)
            {
                largest = len;
            }
        }
        return new Result(files.Length, total, largest);
    }
}
