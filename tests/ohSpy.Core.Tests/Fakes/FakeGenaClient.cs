namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Hand-rolled raw <see cref="TcpClient"/> driver for the Story 4.1 callback host (AC-4.1.23).
/// Connects to a <see cref="Uri"/>'s host:port and sends / withholds bytes for each AC, then reads
/// the response status line. No real device involved — every framing / size / timeout / flood case
/// is driven in-process over a real loopback-style TCP connection to the host's bound adapter IP.
/// </summary>
internal sealed class FakeGenaClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream? _stream;

    public async Task ConnectAsync(Uri callbackBaseUrl)
    {
        var host = IPAddress.Parse(callbackBaseUrl.Host);
        await _client.ConnectAsync(host, callbackBaseUrl.Port).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    /// <summary>Sends the exact bytes of <paramref name="ascii"/> in one write.</summary>
    public Task SendAsync(string ascii) => SendBytesAsync(Encoding.ASCII.GetBytes(ascii));

    public async Task SendBytesAsync(byte[] bytes)
    {
        await _stream!.WriteAsync(bytes).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Drips <paramref name="ascii"/> one byte at a time, pausing <paramref name="gap"/>
    /// between bytes — the slowloris trickle (AC-4.1.24).</summary>
    public async Task DripAsync(string ascii, TimeSpan gap, CancellationToken ct = default)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        foreach (var b in bytes)
        {
            await _stream!.WriteAsync(new[] { b }, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            await Task.Delay(gap, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the whole response (until the server closes the connection) as ASCII.</summary>
    public async Task<string> ReadResponseAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        while (true)
        {
            int read;
            try
            {
                read = await _stream!.ReadAsync(buf, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break; // peer reset on close — treat as end-of-response
            }

            if (read == 0)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(buf, 0, read));
        }

        return sb.ToString();
    }

    /// <summary>Reads only the status line (first line) of the response.</summary>
    public async Task<string> ReadStatusLineAsync(CancellationToken ct = default)
    {
        var full = await ReadResponseAsync(ct).ConfigureAwait(false);
        var nl = full.IndexOf('\n');
        return (nl >= 0 ? full[..nl] : full).TrimEnd('\r');
    }

    /// <summary>True once the server has closed its side (a zero-byte read).</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var buf = new byte[256];
            while (true)
            {
                var read = await _stream!.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
