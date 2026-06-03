namespace ohSpy.Core.Discovery;

using System.Text;
using ohSpy.Core.Diagnostics;

internal sealed class SsdpParser(IDiagnosticEmitter diag)
{
    /// <summary>
    /// Parse a raw SSDP datagram payload. Returns null + emits Warning on malformed.
    /// Both NOTIFY (request-form) and M-SEARCH response (response-form) are accepted.
    /// </summary>
    internal SsdpAnnouncement? Parse(byte[] payload, string remoteEndpoint)
    {
        if (payload.Length == 0)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        var text = Encoding.UTF8.GetString(payload);
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);

        if (lines.Length == 0)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        var firstLine = lines[0].Trim();
        bool isNotify = firstLine.StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase);
        bool isMSearchResponse = firstLine.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase);

        if (!isNotify && !isMSearchResponse)
        {
            EmitParseWarning(remoteEndpoint);
            return null;
        }

        string? nt = null, nts = null, st = null, usn = null, server = null, bootId = null, configId = null;
        Uri? location = null;
        TimeSpan? cacheControlMaxAge = null;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break; // blank line = end of headers

            var colonPos = line.IndexOf(':', StringComparison.Ordinal);
            if (colonPos < 0) continue;

            var key = line[..colonPos].Trim();
            var value = line[(colonPos + 1)..].Trim();

            if (key.Equals("NT", StringComparison.OrdinalIgnoreCase))
                nt = value;
            else if (key.Equals("NTS", StringComparison.OrdinalIgnoreCase))
                nts = value;
            else if (key.Equals("ST", StringComparison.OrdinalIgnoreCase))
                st = value;
            else if (key.Equals("USN", StringComparison.OrdinalIgnoreCase))
                usn = value;
            else if (key.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                _ = Uri.TryCreate(value, UriKind.Absolute, out location);
            else if (key.Equals("CACHE-CONTROL", StringComparison.OrdinalIgnoreCase))
                cacheControlMaxAge = ParseMaxAge(value);
            else if (key.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                server = value;
            else if (key.Equals("BOOTID.UPNP.ORG", StringComparison.OrdinalIgnoreCase))
                bootId = value;
            else if (key.Equals("CONFIGID.UPNP.ORG", StringComparison.OrdinalIgnoreCase))
                configId = value;
            // Unknown headers silently ignored (lenient, D4 vendor-noise philosophy)
        }

        var uuid = ExtractUuid(usn);

        return new SsdpAnnouncement(nt, nts, st, usn, uuid, location, cacheControlMaxAge, server, bootId, configId);
    }

    internal static Guid? ExtractUuid(string? usn)
    {
        if (usn is null) return null;
        var s = usn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? usn[5..] : usn;
        var colonPos = s.IndexOf(':', StringComparison.Ordinal);
        if (colonPos >= 0) s = s[..colonPos];
        return Guid.TryParse(s, out var g) ? g : null;
    }

    private static TimeSpan? ParseMaxAge(string value)
    {
        // e.g. "max-age=1800" or "max-age = 1800"
        var parts = value.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && parts[0].Equals("max-age", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    private void EmitParseWarning(string remoteEndpoint)
    {
        diag.Warning(DiagCategories.SsdpParse, "ssdp parse failed",
            new DiagnosticContext { RemoteEndpoint = remoteEndpoint });
    }
}
