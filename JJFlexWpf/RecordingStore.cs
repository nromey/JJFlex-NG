using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace JJFlexWpf;

/// <summary>
/// Where recordings an operator makes are kept (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// They live at <c>%AppData%\JJFlexRadio\Recordings</c>, one ordinary WAV file
/// each, following the folder convention <see cref="NoiseProfileStore"/>
/// established: a named folder under the app's AppData, plain files inside it,
/// and a Windows Explorer button so they can be renamed, copied and shared
/// with the tools an operator already knows.
/// </para>
/// <para>
/// LOCAL, AND THAT IS A DECISION, NOT A DEFAULT. The radio has its own Digital
/// Voice Keyer with slots that hold recordings, and it is tempting. But DVK
/// slots are STATION state: they belong to whoever owns the rig, they occupy
/// that owner's slots, and every MultiFlex client attached to that radio sees
/// them. A recording an operator makes is OPERATOR state — their voice, their
/// call, their message — and it has to work on a radio they do not own and
/// cannot write to. So it is kept here, on the operator's machine, and travels
/// with the operator. The radio's keyer is an optimisation available to people
/// who own the rig; it is never where this lives.
/// </para>
/// <para>
/// PLAIN WAV, no container of ours, and no required sidecar. A recording is
/// something an operator may well want to play in a media player, send to a
/// friend who says "your audio sounds odd", or attach to a support
/// conversation. Anything we invent to wrap it around gets in the way of all
/// three. A WAV somebody drops into this folder by hand is a first-class
/// recording here, with no import step — which is also how an operator brings
/// a reference file made elsewhere.
/// </para>
/// </remarks>
public static class RecordingStore
{
    /// <summary>File extension for recordings.</summary>
    public const string Extension = ".wav";

    /// <summary>The recordings folder: %AppData%\JJFlexRadio\Recordings.</summary>
    public static string FolderPath => Path.Combine(
        Radios.RadioConfig.AppDataRoot, "Recordings");

    /// <summary>
    /// Where a take goes when the operator has not named it. Recording is one
    /// keystroke and done means done; a save dialog standing between somebody
    /// and their own audio is the friction this project exists to remove. The
    /// timestamp keeps takes from overwriting each other, so a second thought
    /// never costs a first take.
    /// </summary>
    public static string PathForNewTake(DateTime localTime) => Path.Combine(
        FolderPath,
        "take-" + localTime.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + Extension);

    /// <summary>Turn a display name into a file path inside the recordings folder.</summary>
    public static string PathForName(string name) =>
        Path.Combine(FolderPath, SafeFileName(name) + Extension);

    /// <summary>Strip filesystem-hostile characters; never returns empty.</summary>
    public static string SafeFileName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name ?? "")
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)
                sb.Append(c);
        }
        string result = sb.ToString().Trim();
        return result.Length > 0 ? result : "recording";
    }

    /// <summary>A recording on disk, with whatever the file itself can tell us.</summary>
    public sealed class RecordingFile
    {
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public DateTime RecordedLocal { get; init; }
        public double Seconds { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }

        /// <summary>
        /// One spoken or displayed line. Name first, then the things an
        /// operator actually chooses between: how long it is, and when it was
        /// made. Rate and channel count are deliberately absent — they matter
        /// to the code, never to the choice.
        /// </summary>
        public string Describe()
        {
            var parts = new List<string> { string.IsNullOrEmpty(Name) ? "unnamed" : Name };
            if (Seconds > 0) parts.Add(DescribeLength(Seconds));
            if (RecordedLocal != default)
                parts.Add("recorded " + RecordedLocal.ToString(
                    "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// A duration written the way somebody would say it. Seconds up to a
    /// minute, then minutes and seconds — never a bare decimal, which reads
    /// badly out loud and is not what anyone means by "how long is it".
    /// </summary>
    public static string DescribeLength(double seconds)
    {
        if (seconds < 1) return "under a second";
        int total = (int)Math.Round(seconds);
        if (total < 60) return total + (total == 1 ? " second" : " seconds");
        int m = total / 60, s = total % 60;
        string mm = m + (m == 1 ? " minute" : " minutes");
        if (s == 0) return mm;
        return mm + " " + s + (s == 1 ? " second" : " seconds");
    }

    /// <summary>
    /// Every readable recording in the folder, newest first. Unreadable files
    /// are skipped and traced, never thrown — one bad WAV must not take the
    /// list down with it.
    /// </summary>
    public static List<RecordingFile> Enumerate()
    {
        var result = new List<RecordingFile>();
        try
        {
            if (!Directory.Exists(FolderPath)) return result;
            foreach (string file in Directory.GetFiles(FolderPath, "*" + Extension))
            {
                try
                {
                    var info = new FileInfo(file);
                    WavInfo.TryRead(file, out WavInfo wav);
                    result.Add(new RecordingFile
                    {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file),
                        RecordedLocal = info.LastWriteTime,
                        Seconds = wav.Seconds,
                        SampleRate = wav.SampleRate,
                        Channels = wav.Channels,
                    });
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"RecordingStore: skipping {file}: {ex.Message}");
                }
            }
            result.Sort((a, b) => b.RecordedLocal.CompareTo(a.RecordedLocal));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RecordingStore.Enumerate failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// The most recent recording in the folder, or null when there is none.
    /// </summary>
    /// <remarks>
    /// <b>Task #455 — this exists so that two buttons cannot hold two opinions
    /// about where takes live.</b> "Play last take" answered "no recording yet"
    /// while "Open recordings folder", one section away in the same dialog,
    /// showed the files and another program played them. The two were asking
    /// different stores: the folder button asked this class, and the play
    /// button asked the RADIO's quick-record buffer, which is empty unless an
    /// audio check has just recorded into it. Nothing was broken in either
    /// path; the word "take" simply meant two things.
    /// <para>
    /// So the question "what is the last take?" is asked HERE, of the store the
    /// folder button opens, by every caller that needs it. Adding a caller does
    /// not add an opinion.
    /// </para>
    /// </remarks>
    public static RecordingFile? Newest()
    {
        var all = Enumerate();          // already sorted newest first
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// Open the recordings folder in File Explorer, creating it first, so
    /// recordings can be played, renamed, shared or deleted with ordinary
    /// tools.
    /// </summary>
    public static bool OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            Process.Start(new ProcessStartInfo { FileName = FolderPath, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RecordingStore.OpenFolder failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Delete a recording. Returns false and traces on failure.</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RecordingStore.Delete failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// The handful of facts a WAV header carries, read without decoding the audio.
/// </summary>
/// <remarks>
/// Listing a folder must not cost the time and memory of decoding every file
/// in it, and the only questions a list needs answered — how long, what rate,
/// how many channels — are all in the first few dozen bytes.
/// </remarks>
public readonly struct WavInfo
{
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public double Seconds { get; init; }

    /// <summary>
    /// Read the header. Returns false, with a default result, for anything
    /// that is not a PCM or float WAV we understand.
    /// </summary>
    public static bool TryRead(string path, out WavInfo info)
    {
        info = default;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new BinaryReader(fs);

            if (new string(r.ReadChars(4)) != "RIFF") return false;
            r.ReadUInt32(); // riff size
            if (new string(r.ReadChars(4)) != "WAVE") return false;

            int rate = 0, channels = 0, bits = 0;
            long dataBytes = 0;

            while (fs.Position + 8 <= fs.Length)
            {
                string id = new string(r.ReadChars(4));
                uint size = r.ReadUInt32();
                long next = fs.Position + size + (size % 2); // chunks are word-aligned

                if (id == "fmt ")
                {
                    r.ReadUInt16();                 // format tag
                    channels = r.ReadUInt16();
                    rate = (int)r.ReadUInt32();
                    r.ReadUInt32();                 // byte rate
                    r.ReadUInt16();                 // block align
                    bits = r.ReadUInt16();
                }
                else if (id == "data")
                {
                    dataBytes = size;
                }

                if (next <= fs.Position && id != "data") break; // malformed; stop rather than spin
                if (next >= fs.Length) break;
                fs.Position = next;
            }

            if (rate <= 0 || channels <= 0 || bits <= 0) return false;
            double seconds = dataBytes / (double)(rate * channels * (bits / 8.0));
            info = new WavInfo
            {
                SampleRate = rate,
                Channels = channels,
                BitsPerSample = bits,
                Seconds = seconds,
            };
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"WavInfo.TryRead failed for {path}: {ex.Message}");
            return false;
        }
    }
}
