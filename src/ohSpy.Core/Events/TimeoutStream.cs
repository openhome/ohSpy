namespace ohSpy.Core.Events;

/// <summary>
/// Read-only <see cref="Stream"/> wrapper that enforces a settable <em>idle-time</em> budget
/// on every read — the "one place to enforce timeout discipline" of Decision 4 (L467-469).
/// The hand-rolled parser sets <see cref="ActiveBudget"/> as it transitions phase
/// (headers → body), so a single wrapper covers both the AC-4.1.7 header budget and the
/// AC-4.1.8 body budget.
/// <para>
/// <b>Idle-time model.</b> Each <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> arms a
/// per-read linked CTS with <c>CancelAfter(ActiveBudget)</c>. Slowloris (a trickle that never
/// completes a read within the budget) trips the timer. The budget is per-read, so a steady
/// stream of small-but-prompt reads never trips it — exactly "idle time exceeds the budget"
/// (AC-4.1.9), not a wall-clock cap on the whole phase.
/// </para>
/// <para>
/// <b>Cancellation composition (D4↔D7).</b> The caller token (linked to the adapter/app CTS,
/// AC-4.1.21) is linked into the per-read CTS too, so an adapter/app cancel <em>also</em>
/// unblocks a pending read. On unblock we disambiguate: if the caller token is cancelled, this
/// is a genuine shutdown → rethrow <see cref="OperationCanceledException"/> (the host swallows
/// it as the normal teardown path, no diagnostic). Otherwise the budget timer fired → throw the
/// distinguishable <see cref="CallbackTimeoutException"/> sentinel. Shutdown therefore always
/// wins over timeout, and the two are never confused.
/// </para>
/// <para><c>internal sealed</c> — InternalsVisibleTo covers Tests + App.</para>
/// </summary>
internal sealed class TimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;

    /// <summary>The active idle-read budget. The parser flips this from the header budget to
    /// the body budget when it crosses the empty-CRLF terminator.</summary>
    public TimeSpan ActiveBudget { get; set; }

    public TimeoutStream(Stream inner, TimeSpan initialBudget, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        ActiveBudget = initialBudget;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Sync read is intentionally unsupported — the host is fully async (AsyncDisciplineTests / Pattern 6).
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var perRead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ActiveBudget > TimeSpan.Zero)
        {
            perRead.CancelAfter(ActiveBudget);
        }

        try
        {
            return await _inner.ReadAsync(buffer, perRead.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && perRead.IsCancellationRequested)
        {
            // The per-read budget timer fired (the caller token is NOT the cause) → idle-read
            // overrun. Surface the distinguishable sentinel so the host maps it to HeadersTo/BodyTo.
            throw new CallbackTimeoutException("read idle time exceeded the active callback budget");
        }
        // OperationCanceledException with the caller token cancelled propagates unchanged — the
        // host treats it as the normal shutdown path (swallow, no diagnostic).
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
