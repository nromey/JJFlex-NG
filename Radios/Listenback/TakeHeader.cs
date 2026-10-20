using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Radios.Listenback;

/// <summary>Append-only, flushed facts. A close line is a commit record, written last.</summary>
public sealed class TakeHeader : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    public TakeHeader(string path, string format = "jjflex-iq-take-1")
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        Write("format", format);
    }
    public void Write(string key, object value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['=', '\n', '\r']) >= 0)
            throw new ArgumentException(nameof(key));
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Ordinary values remain plain readable text; escapes make arbitrary
        // names lossless without allowing a name to forge a closing record.
        text = text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        lock (_gate) _writer.WriteLine(key + "=" + text);
    }
    public void Event<T>(string key, T value) => Write(key, JsonSerializer.Serialize(value));
    public void Dispose() { lock (_gate) _writer.Dispose(); }

    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (string line in File.ReadLines(path))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("header-line");
            var value = new StringBuilder();
            string raw = line[(equals + 1)..];
            for (int i = 0; i < raw.Length; ++i)
            {
                if (raw[i] != '\\') value.Append(raw[i]);
                else
                {
                    if (++i == raw.Length) throw new InvalidDataException("header-escape");
                    value.Append(raw[i] switch { 'r' => '\r', 'n' => '\n', '\\' => '\\',
                        _ => throw new InvalidDataException("header-escape") });
                }
            }
            result.Add(new(line[..equals], value.ToString()));
        }
        return result;
    }
    public static string Checksum(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}

public sealed record TakeInspection(string State, string Reason,
    IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public string Get(string key) => Fields.LastOrDefault(x => x.Key == key).Value;
    public bool Playable => State != "corrupt" && Get("classifier") == "noise-like";
}

public static class TakeReader
{
    public static TakeInspection Inspect(string directory)
    {
        IReadOnlyList<KeyValuePair<string, string>> fields = Array.Empty<KeyValuePair<string, string>>();
        try
        {
            fields = TakeHeader.Read(Path.Combine(directory, "take.txt"));
            var result = new TakeInspection("incomplete", "no-closing-block", fields);
            if (result.Get("format") != "jjflex-iq-take-1") return result with { State = "corrupt", Reason = "format" };
            string iq = Path.Combine(directory, "rf.iq");
            long length = new FileInfo(iq).Length;
            if (length % 8 != 0) return result with { State = "corrupt", Reason = "partial-iq-pair" };
            string sum = result.Get("sha256");
            if (sum != null && sum != TakeHeader.Checksum(iq)) return result with { State = "corrupt", Reason = "checksum" };
            string count = result.Get("pairs-written");
            if (count != null && (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out long pairs)
                || pairs != length / 8)) return result with { State = "corrupt", Reason = "length" };
            // Also inspect partial takes, which have no closing checksum yet.
            double.TryParse(result.Get("quantization-step"), NumberStyles.Float, CultureInfo.InvariantCulture, out double step);
            long.TryParse(result.Get("quantization-start-pair"), out long gridStart);
            int.TryParse(result.Get("quantization-floats"), out int gridCount);
            var blocks = new List<(long Start, long End, double Step)>();
            foreach (var field in fields.Where(f => f.Key == "payload-block"))
            {
                using var json = JsonDocument.Parse(field.Value);
                long start = checked(json.RootElement.GetProperty("AtPair").GetInt64() * 2);
                long end = checked(start + json.RootElement.GetProperty("Floats").GetInt32());
                double grid = json.RootElement.GetProperty("GridStep").GetDouble();
                if (start < 0 || end < start || grid < 0 || !double.IsFinite(grid)) throw new InvalidDataException("quantization-range");
                if (result.Get("closed") != null && end > length / 4) throw new InvalidDataException("quantization-range");
                blocks.Add((start, end, grid));
            }
            if (blocks.Count == 0 && step > 0) blocks.Add((gridStart * 2, gridStart * 2 + gridCount, step));
            blocks.Sort((a, b) => a.Start.CompareTo(b.Start));
            int block = 0;
            using (var input = new BinaryReader(File.OpenRead(iq)))
                for (long i = 0; i < length / 4; ++i)
                {
                    float value = input.ReadSingle();
                    if (!float.IsFinite(value)) return result with { State = "corrupt", Reason = "non-finite" };
                    while (block < blocks.Count && i >= blocks[block].End) block++;
                    double blockStep = block < blocks.Count && i >= blocks[block].Start ? blocks[block].Step : 0;
                    if (blockStep > 0 && Math.Abs(value / blockStep - Math.Round(value / blockStep)) > 1e-6)
                        return result with { State = "corrupt", Reason = "quantization" };
                }
            if (result.Get("closed") == null) return result;
            if (fields[^1].Key != "closed" || sum == null || count == null)
                return result with { State = "corrupt", Reason = "closing-block" };
            if (result.Get("classifier") != "noise-like") return result with { State = "corrupt", Reason = "classifier" };
            return result.Get("closed") == "clean"
                ? result with { State = "clean", Reason = "" }
                : result with { Reason = result.Get("end-reason") ?? "gaps-or-overrun" };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is JsonException || ex is OverflowException)
        { return new("corrupt", ex.GetType().Name, fields); }
    }
}
