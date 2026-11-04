#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace Radios.Listenback;

/// <summary>
/// Take folders a live obligation of THIS run still points at: a saved
/// playback whose slice mutes are not yet put back (Unified takes, D2, and
/// design section 8's origin link). The deletion guard's busy test asks here,
/// beside the recorder's own "still capturing". Nothing here is on disk: slice
/// mutes belong to the connection and end with it.
/// </summary>
public static class TakeObligations
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Owed = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>The folder is owed something until the returned token is disposed.</summary>
    public static IDisposable Owe(string folder)
    {
        string key = Key(folder);
        lock (Gate) Owed[key] = Owed.TryGetValue(key, out int n) ? n + 1 : 1;
        return new Token(key);
    }

    /// <summary>True while anything is still owed for <paramref name="folder"/>.</summary>
    public static bool IsOwed(string folder)
    {
        string key = Key(folder);
        lock (Gate) return Owed.ContainsKey(key);
    }

    private sealed class Token : IDisposable
    {
        private string? _key;
        public Token(string key) => _key = key;
        public void Dispose()
        {
            string? key = System.Threading.Interlocked.Exchange(ref _key, null);
            if (key == null) return;
            lock (Gate)
                if (Owed.TryGetValue(key, out int n)) { if (n <= 1) Owed.Remove(key); else Owed[key] = n - 1; }
        }
    }
}
