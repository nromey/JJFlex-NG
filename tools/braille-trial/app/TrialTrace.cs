using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace JJBrailleTrial;

/// <summary>
/// The trial's trace: one timestamped line per Hello, Route, Command, carrier
/// update, caret move and tuning step, written to jjbrailletrial-trace.txt
/// beside the executable and shown in the window's read-only list. Nothing
/// else is recorded. The file is appended to, with one line marking where each
/// run starts, so a session's history survives a restart of the program.
/// </summary>
public sealed class TrialTrace : IDisposable
{
    private const int ListLimit = 1000;
    private readonly StreamWriter? _writer;

    public TrialTrace()
    {
        Lines = new ObservableCollection<string>();
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "jjbrailletrial-trace.txt");
        try
        {
            _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (IOException)
        {
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    public string Path { get; }

    public bool FileOpen => _writer != null;

    public ObservableCollection<string> Lines { get; }

    /// <summary>Write one line. Tabs and line feeds inside details are shown as \t and \n.</summary>
    public void Write(string kind, string details)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string safe = details.Replace("\t", "\\t", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
        string line = stamp + "  " + kind + "  " + safe;
        _writer?.WriteLine(line);
        Lines.Add(line);
        while (Lines.Count > ListLimit)
        {
            Lines.RemoveAt(0);
        }
    }

    public void Dispose() => _writer?.Dispose();
}
