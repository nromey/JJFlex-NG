using System.Globalization;

namespace JJFlexUpdaterHelper;

// Append-only forensic record for the helper's run. Lives at
// <staging-dir>/helper.log. On success the file is removed by step 9
// alongside the rest of the staging dir; on failure the file persists
// so the user (or a Track C bug-bundle attached to a failed update)
// has a complete, timestamped record of what the helper did and when.
//
// Format: one line per event, ISO-8601-UTC timestamp + level + message.
// Forward-readable in any text editor; trivially grep-able by support.
internal sealed class HelperLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly Lock _gate = new();
    private bool _disposed;

    public HelperLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _writer = null;
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // FileShare.Read so a curious user can tail the log mid-run.
            // Append mode so re-running the helper against the same staging
            // dir (e.g. to investigate an earlier failure) layers on rather
            // than clobbering history.
            var stream = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream)
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a log we can't open is not worth crashing the helper over.
            // Console.Error captures the loss for the operator if anyone is watching.
            Console.Error.WriteLine($"helper.log: could not open '{path}' ({ex.Message}); continuing without file log.");
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        if (_writer is null) return;

        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var line = $"{ts} [{level}] {message}";

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already past best-effort; swallow to avoid cascading failures.
                Console.Error.WriteLine($"helper.log: write failed ({ex.Message}); further log lines will be lost.");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}

// Pairs a HelperLog with Console output so a single callback fans messages
// to both channels. Steps just take Action<string> log / Action<string> warn
// callbacks; they don't need to know whether a file log exists.
//
// CloseFileLog() releases the helper.log handle while keeping the console
// channel live — needed before step 9's CleanupStep tries to delete the
// staging dir, which contains the file we're writing to. After CloseFileLog,
// Info/Warn still emit to the console.
internal sealed class Logger : IDisposable
{
    private HelperLog? _file;

    public Logger(string? logFilePath)
    {
        _file = string.IsNullOrWhiteSpace(logFilePath) ? null : new HelperLog(logFilePath);
    }

    public void Info(string message)
    {
        Console.Out.WriteLine(message);
        _file?.Info(message);
    }

    public void Warn(string message)
    {
        Console.Error.WriteLine(message);
        _file?.Warn(message);
    }

    public void CloseFileLog()
    {
        _file?.Dispose();
        _file = null;
    }

    public void Dispose() => CloseFileLog();
}
