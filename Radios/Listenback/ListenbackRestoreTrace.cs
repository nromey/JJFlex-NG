using System;
using System.IO;
using System.Text.Json;

namespace Radios.Listenback;

/// <summary>Restoration happens after take.txt closes. Keep each attempt beside it.</summary>
internal sealed class ListenbackRestoreTrace
{
    private readonly string _path;
    private readonly object _gate = new();
    internal ListenbackRestoreTrace(RadioRecoverySnapshot snapshot)
    {
        if (Path.IsPathFullyQualified(snapshot.TakeDirectory ?? "") && Directory.Exists(snapshot.TakeDirectory))
            _path = Path.Combine(snapshot.TakeDirectory, "restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".txt");
        Write("attempt", new { snapshot.Serial, snapshot.ConnectionId, snapshot.ResourceCreationPending });
    }
    internal void Write(string phase, object detail)
    {
        string line = JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Phase = phase, Detail = detail });
        JJTrace.Tracing.TraceLine("Listenback restore: " + line, System.Diagnostics.TraceLevel.Info);
        if (_path == null) return;
        try { lock (_gate) File.AppendAllText(_path, line + Environment.NewLine); }
        catch (Exception ex) { JJTrace.Tracing.TraceLine("Listenback restore trace unavailable: " + ex.Message, System.Diagnostics.TraceLevel.Error); }
    }
}
