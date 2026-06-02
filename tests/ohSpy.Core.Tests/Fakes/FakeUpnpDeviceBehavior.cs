namespace ohSpy.Core.Tests.Fakes;

/// <summary>
/// Failure-injection modes for <see cref="FakeUpnpDevice"/>. Story 1.6 ships the
/// three minimum modes (Happy + two hang scenarios); extended modes
/// (SlowDripBody, GiantScpd, ChunkedThenAbort, FaultResponse, WrongContentLength —
/// per D3) will land in a follow-up story when actually needed by a chaos test.
/// </summary>
internal enum FakeUpnpDeviceBehavior
{
    /// <summary>Normal 200 OK + canned XML body. Used as a positive control.</summary>
    Happy,

    /// <summary>
    /// Accept the TCP connection but never send response headers — the request
    /// handler awaits an unresolved <see cref="System.Threading.Tasks.Task"/>.
    /// Used to verify connect-timeout discipline.
    /// </summary>
    HangBeforeHeaders,

    /// <summary>
    /// Send 200 OK + headers, then dangle the response body forever. The body-read
    /// must hit the per-op linked-CTS budget and throw <c>UpnpTimeoutException</c>.
    /// <para>
    /// This is the AC-3.5 / AC-13.4 regression test — the prior tool's actual
    /// defect was a body read that never completed after headers arrived.
    /// </para>
    /// </summary>
    HangAfter200Ok,
}
