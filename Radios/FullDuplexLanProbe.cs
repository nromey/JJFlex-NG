using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Radios.Listenback;

namespace Radios;

/// <summary>A short, read-only LAN API connection that sends no GUI registration command.</summary>
internal static class FullDuplexLanProbe
{
    internal static async Task<bool?> ReadAsync(IPAddress address, CancellationToken cancellation)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, 4992, cancellation).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        // First and only command. A fresh connection gets the initial radio
        // status; the 8600 does not resend it on a repeated subscription.
        await stream.WriteAsync(Encoding.ASCII.GetBytes("C1|sub radio all\n"), cancellation).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
            if (line == null) return null;
            bool? value = Parse(line);
            if (value.HasValue) return value;
        }
    }

    internal static bool? Parse(string line)
    {
        if (line.Length < 3 || line[0] != 'S') return null;
        int bar = line.IndexOf('|');
        if (bar < 0 || !line[(bar + 1)..].StartsWith("radio ", StringComparison.OrdinalIgnoreCase)) return null;
        var fields = RadioStatusTap.Values(line[(bar + 1)..]);
        if (!fields.TryGetValue("full_duplex_enabled", out string value)) return null;
        return value switch { "0" => false, "1" => true, _ => null };
    }
}
