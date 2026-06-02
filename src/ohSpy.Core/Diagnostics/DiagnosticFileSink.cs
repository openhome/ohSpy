namespace ohSpy.Core.Diagnostics;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

/// <summary>
/// On-disk rolling-log impl of <see cref="IDiagnosticFileSink"/>.
/// <para>
/// Story 1.5 amendment (A14 candidate): the original spec placed this in
/// <c>ohSpy.App</c> because of <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>,
/// but that API is plain BCL and works fine on the Core's <c>net10.0</c> target. Moving the
/// type into Core (a) lets the <c>net10.0</c> test project consume it without a TFM bump or
/// multi-targeting, and (b) eliminates the App-side <c>InternalsVisibleTo</c> dance for the
/// test-only ctor. The App still owns DI registration; Core owns the impl.
/// </para>
/// <para>
/// Architecture: channel (capacity 1000, DropOldest) + background pump task. Production
/// path: <c>%LOCALAPPDATA%\ohSpy\diagnostics\ohSpy-yyyyMMdd.log</c>. Rotates at 2 MB;
/// retains 8 files (≤ 16 MB total). Startup failure emits ONE warning via the late-bound
/// ring sink (AC-8.6) and degrades to no-op. Mid-session I/O failure logs to MEL and
/// degrades silently (never recurses through <see cref="IDiagnosticEmitter"/>).
/// </para>
/// </summary>
internal sealed class DiagnosticFileSink : IDiagnosticFileSink
{
    private const int ChannelCapacity = 1000;
    private const long MaxFileBytes = 2L * 1024 * 1024; // 2 MB per AC-8.5
    private const int MaxRetainedFiles = 8;             // ≤ 16 MB total on disk

    // JsonSerializerOptions hot-path: write one line per entry. Allocate ONCE as a static
    // field — JsonSerializerOptions is internally cached after first use; mutating it later
    // is a perf hit. WhenWritingNull skips null DiagnosticContext fields. WriteIndented MUST
    // stay false — JSON-lines requires ONE physical line per entry.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private readonly ILogger<DiagnosticFileSink> _logger;
    private readonly Channel<DiagnosticEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _diagnosticsDir;
    private readonly Task _pumpTask;
    private readonly TaskCompletionSource _ringSinkAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDiagnosticRingSink? _ringSink;
    private FileStream? _currentFile;
    private long _currentFileBytes;
    private DateTime _currentFileDate;
    private volatile bool _disabled;

    /// <summary>
    /// Production ctor — resolves the real <c>%LOCALAPPDATA%</c> path. Delegates to the
    /// test-only ctor for actual field init.
    /// </summary>
    public DiagnosticFileSink(ILogger<DiagnosticFileSink> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ohSpy",
            "diagnostics"))
    {
    }

    /// <summary>
    /// Test-only ctor — accepts the diagnostics directory directly so tests can use a
    /// temp dir without polluting the dev's actual <c>%LOCALAPPDATA%\ohSpy\diagnostics\</c>.
    /// </summary>
    internal DiagnosticFileSink(ILogger<DiagnosticFileSink> logger, string diagnosticsDir)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(diagnosticsDir);
        _logger = logger;
        _diagnosticsDir = diagnosticsDir;
        _channel = Channel.CreateBounded<DiagnosticEntry>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>
    /// Late-bind the ring sink for the startup-failure path (AC-8.6). The emitter
    /// fan-out + DI graph have a circular dependency potential: file sink wants to emit
    /// to ring sink on failure, but at file sink ctor time the ring sink hasn't been
    /// resolved yet. The App's composition root calls this method after building the
    /// service provider, BEFORE the bootstrap is fully complete.
    /// </summary>
    internal void SetRingSink(IDiagnosticRingSink ringSink)
    {
        ArgumentNullException.ThrowIfNull(ringSink);
        _ringSink = ringSink;
        _ringSinkAvailableTcs.TrySetResult();
    }

    public void Push(DiagnosticEntry entry)
    {
        if (_disabled)
        {
            return;
        }
        // TryWrite returns false ONLY if the channel is completed (post-shutdown). The
        // DropOldest channel mode means it never returns false on capacity overflow —
        // it silently discards the oldest entry to make room for the new one.
        _channel.Writer.TryWrite(entry);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        // Step 1: try to open the file. If this fails, emit ONE warning to the ring sink
        // (AC-8.6) and degrade to no-op.
        try
        {
            Directory.CreateDirectory(_diagnosticsDir);
            OpenOrAppendToToday();
        }
        catch (Exception ex)
        {
            _disabled = true;
            await EmitRingSinkUnavailableAsync(ex.Message).ConfigureAwait(false);
            // Drain the channel to avoid back-pressure; just discard.
            try
            {
                await foreach (var _ in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                }
            }
            catch (OperationCanceledException)
            {
            }
            return;
        }

        // Step 2: pump loop.
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    try
                    {
                        await WriteEntryAsync(entry, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // I/O failure mid-session: log to MEL (don't recurse to emitter)
                        // and degrade silently. The ring sink continues to work; the
                        // diagnostic stream just stops persisting.
                        //
                        // CA1848 suppression: this is a once-per-session degrade path; the
                        // source-generated LoggerMessage delegate ceremony isn't warranted
                        // for a single bounded log call.
#pragma warning disable CA1848
                        _logger.LogWarning(ex, "DiagnosticFileSink write failure; disabling file persistence for the session");
#pragma warning restore CA1848
                        _disabled = true;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            /* shutdown */
        }
    }

    private async Task WriteEntryAsync(DiagnosticEntry entry, CancellationToken ct)
    {
        // Rotate if a new day has rolled over (e.g. dev runs the app overnight).
        if (entry.TimestampUtc.Date != _currentFileDate)
        {
            await RotateToTodayAsync(ct).ConfigureAwait(false);
        }
        // Rotate if the current file has hit 2 MB.
        if (_currentFileBytes >= MaxFileBytes)
        {
            await RotateToTodayAsync(ct).ConfigureAwait(false);
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                ts = entry.TimestampUtc,
                sev = entry.Severity.ToString(),
                cat = entry.Category,
                msg = entry.Message,
                ctx = entry.Context,
            },
            JsonOptions);

        await _currentFile!.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        await _currentFile.WriteAsync(NewlineBytes, ct).ConfigureAwait(false);
        await _currentFile.FlushAsync(ct).ConfigureAwait(false);
        _currentFileBytes += jsonBytes.Length + NewlineBytes.Length;
    }

    private void OpenOrAppendToToday()
    {
        var today = DateTime.UtcNow.Date;
        var fileName = $"ohSpy-{today:yyyyMMdd}.log";
        var fullPath = Path.Combine(_diagnosticsDir, fileName);
        _currentFile = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentFileBytes = _currentFile.Length;
        _currentFileDate = today;
    }

    private async Task RotateToTodayAsync(CancellationToken ct)
    {
        // Close current file; OpenOrAppendToToday opens (or creates) today's file.
        if (_currentFile is not null)
        {
            await _currentFile.DisposeAsync().ConfigureAwait(false);
            _currentFile = null;
        }

        // On a size-cap rotation within the same day, we can't reuse today's file name
        // because we'd just reopen the very file we capped. Instead, rename today's file
        // to a sequenced sibling and open a fresh today.log.
        if (DateTime.UtcNow.Date == _currentFileDate)
        {
            RotateCurrentDateFileToSequenced();
        }

        // Enforce retention BEFORE opening the new file (otherwise we'd open then immediately
        // delete it if we somehow exceeded the count).
        PruneOldFiles();

        OpenOrAppendToToday();
    }

    // Rename today's filled file to a sequenced sibling — e.g.
    //   ohSpy-20260602.log → ohSpy-20260602-001.log
    //   ohSpy-20260602.log → ohSpy-20260602-002.log  (if -001 already exists)
    // so a fresh ohSpy-<today>.log can be opened. Tolerant of races / locks.
    private void RotateCurrentDateFileToSequenced()
    {
        var today = _currentFileDate;
        var basePath = Path.Combine(_diagnosticsDir, $"ohSpy-{today:yyyyMMdd}.log");
        if (!File.Exists(basePath))
        {
            return;
        }

        try
        {
            for (int seq = 1; seq <= 999; seq++)
            {
                var candidate = Path.Combine(_diagnosticsDir, $"ohSpy-{today:yyyyMMdd}-{seq:D3}.log");
                if (File.Exists(candidate))
                {
                    continue;
                }
                File.Move(basePath, candidate);
                return;
            }
        }
        catch
        {
            /* tolerate move failures; OpenOrAppendToToday will just continue appending */
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            // Tightened glob: ohSpy-yyyyMMdd.log AND ohSpy-yyyyMMdd-NNN.log only — won't
            // sweep arbitrary ohSpy-*.log files a user / sysadmin may have placed in the
            // directory. Lex-order = chronological for both shapes (date prefix dominates).
            //
            // We prune to MaxRetainedFiles - 1 because the IMMEDIATE next step is
            // OpenOrAppendToToday() which creates the new active file — bringing the total
            // back to MaxRetainedFiles. Pruning to MaxRetainedFiles would let the directory
            // reach MaxRetainedFiles + 1 after the open, violating AC-6's ≤ 8 cap.
            var files = Directory.GetFiles(_diagnosticsDir, "ohSpy-????????*.log")
                                 .OrderBy(p => p, StringComparer.Ordinal)
                                 .ToArray();
            const int RetainAfterPrune = MaxRetainedFiles - 1;
            if (files.Length <= RetainAfterPrune)
            {
                return;
            }
            foreach (var stale in files.Take(files.Length - RetainAfterPrune))
            {
                try
                {
                    File.Delete(stale);
                }
                catch
                {
                    /* tolerate concurrent locks */
                }
            }
        }
        catch
        {
            /* enumeration failure is non-fatal */
        }
    }

    private async Task EmitRingSinkUnavailableAsync(string errorText)
    {
        // Ring sink may not be late-bound yet; wait briefly. Skip emission if it never
        // becomes available — the App has bigger problems at that point.
        //
        // VSTHRD003 suppression: _ringSinkAvailableTcs is intentionally signalled from
        // outside this method (App.OnLaunched calls SetRingSink which calls TrySetResult).
        // We are NOT inside a JoinableTaskFactory context — the pump runs on a plain
        // Task.Run-scheduled thread-pool thread. The 5-second WhenAny timeout bounds the
        // wait regardless.
#pragma warning disable VSTHRD003
        await Task.WhenAny(_ringSinkAvailableTcs.Task, Task.Delay(5_000)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _ringSink?.Push(new DiagnosticEntry(
            DateTime.UtcNow,
            DiagSeverity.Warning,
            DiagCategories.DiagnosticsFileSinkUnavailable,
            "diagnostic file sink unavailable; file persistence disabled for this session",
            new DiagnosticContext { ErrorText = errorText }));
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        // Drain pending entries with the 5 s budget from the design contract.
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct);
        combined.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _pumpTask.WaitAsync(combined.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* budget exceeded; force-shutdown */
        }
        if (_currentFile is not null)
        {
            await _currentFile.DisposeAsync().ConfigureAwait(false);
            _currentFile = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }
}
