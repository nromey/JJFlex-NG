#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The radios an audio check left without putting back their power and
    /// antenna settings at a disconnect: because the radio was still
    /// transmitting ("withheld"), or because the restore could not be confirmed
    /// ("unconfirmed") (#835, fix round 2026-10-29, item 2). One radio per line,
    /// its serial then the reason, in a small file under the settings root, so
    /// the reminder survives the FlexBase being disposed and made again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the bench (2026-10-29 02:16-02:18) the warning was spoken at the
    /// disconnect and Noel never heard it: the disconnect moves focus between
    /// windows, and a window change flushes the screen reader where nothing of
    /// ours can see it. So it is said again at the next connect to that radio,
    /// through the connect briefing, with the warning alarm.
    /// </para>
    /// <para>
    /// The same shape as <see cref="SendMonitorPutBackStore"/>, kept separate on
    /// purpose: that store is cleared by a radio acknowledgement, this one by
    /// being said. A read or write failure is traced and reads as "nothing
    /// recorded"; a reminder must never stop a connect.
    /// </para>
    /// <para>
    /// <b>What to put back (fix round 1030, ruled 2026-10-29 21:13).</b> Beside
    /// the reason, the same file keeps one line per setting the check changed
    /// and did not put back (<see cref="CheckPutBack"/>): tab-separated, the
    /// serial, the marker <c>put-back</c>, the setting, its instance, the
    /// original value and the value the check set. Those lines are cleared one
    /// at a time, when the radio has answered the put-back of that setting or
    /// when the setting was left alone, the discipline
    /// <see cref="SendMonitorPutBackStore"/> follows; the reason line is
    /// cleared, as before, once the sentence has been handed to speech. A file
    /// written before this round has reason lines only, and reads as before.
    /// One store, not a third: the reason and the settings are one record of
    /// one disconnect, and two files could disagree about it.
    /// </para>
    /// </remarks>
    public sealed class CheckRestoreReminderStore
    {
        private const string FileName = "checkRestoreReminders.txt";
        private static readonly object Gate = new();
        private readonly string _path;

        /// <summary>A store in <paramref name="directory"/>; production passes <see cref="RadioConfig.AppDataRoot"/>.</summary>
        public CheckRestoreReminderStore(string directory)
        {
            _path = Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), FileName);
        }

        private const string PutBackMarker = "put-back";
        private static readonly char[] NotInAValue = { '\t', '\r', '\n' };

        /// <summary>
        /// Why a reminder is owed to this radio ("withheld", "unconfirmed", or
        /// the outcome of a put-back not yet said), or null when none is.
        /// </summary>
        public string? ReasonFor(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (Gate) return Find(Read(), serial.Trim())?.Reason;
        }

        /// <summary>True while anything is owed to this radio: a reminder, or a setting to put back.</summary>
        public bool Contains(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            lock (Gate) return Read().Any(l => Same(l, serial.Trim()));
        }

        /// <summary>Owe this radio a reminder. A later reason replaces an earlier one; recorded settings stay.</summary>
        public void Add(string? serial, string reason)
        {
            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(reason)) return;
            lock (Gate)
            {
                var lines = Read();
                lines.RemoveAll(l => Same(l, serial.Trim()) && !IsPutBack(l));
                lines.Add(serial.Trim() + " " + reason.Trim());
                Write(lines);
            }
        }

        /// <summary>Forget everything owed to this radio, the reminder and any setting still to put back.</summary>
        public void Remove(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            lock (Gate)
            {
                var lines = Read();
                if (lines.RemoveAll(l => Same(l, serial.Trim())) == 0) return;
                Write(lines);
            }
        }

        /// <summary>The reminder has been said: forget it, and keep any setting still to put back.</summary>
        public void RemoveReason(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            lock (Gate)
            {
                var lines = Read();
                if (lines.RemoveAll(l => Same(l, serial.Trim()) && !IsPutBack(l)) == 0) return;
                Write(lines);
            }
        }

        /// <summary>
        /// Record settings to put back at the next connect to this radio. A
        /// setting already recorded keeps its ORIGINAL (the older record is the
        /// operator's value) and takes the newer check's value; a setting the
        /// check did not really change is not recorded.
        /// </summary>
        public void AddPutBacks(string? serial, IEnumerable<CheckPutBack> putBacks)
        {
            if (string.IsNullOrWhiteSpace(serial) || putBacks == null) return;
            string s = serial.Trim();
            lock (Gate)
            {
                var lines = Read();
                var held = PutBacks(lines, s);
                bool changed = false;
                foreach (var item in putBacks)
                {
                    if (item == null || !Valid(item)) continue;
                    int at = held.FindIndex(h => h.SameSettingAs(item));
                    var merged = at < 0 ? item : held[at] with { CheckSet = item.CheckSet };
                    if (at >= 0) held.RemoveAt(at);
                    if (merged.Changed) held.Add(merged);
                    changed = true;
                }
                if (!changed) return;
                lines.RemoveAll(l => Same(l, s) && IsPutBack(l));
                lines.AddRange(held.Select(h => Line(s, h)));
                Write(lines);
            }
        }

        /// <summary>The settings still to put back on this radio, in the order they were recorded.</summary>
        public IReadOnlyList<CheckPutBack> PutBacksFor(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return Array.Empty<CheckPutBack>();
            lock (Gate) return PutBacks(Read(), serial.Trim());
        }

        /// <summary>This setting was put back (the radio answered) or left alone: forget it.</summary>
        public void RemovePutBack(string? serial, CheckPutBack item)
        {
            if (string.IsNullOrWhiteSpace(serial) || item == null) return;
            string s = serial.Trim();
            lock (Gate)
            {
                var lines = Read();
                if (lines.RemoveAll(l => Same(l, s) && Parse(l) is { } p && p.SameSettingAs(item)) == 0) return;
                Write(lines);
            }
        }

        private static bool Valid(CheckPutBack item) =>
            !string.IsNullOrWhiteSpace(item.Setting) && item.Original != null && item.CheckSet != null && item.Target != null &&
            !new[] { item.Setting, item.Target, item.Original, item.CheckSet }.Any(v => v.IndexOfAny(NotInAValue) >= 0);

        private static string Line(string serial, CheckPutBack item) =>
            string.Join("\t", serial, PutBackMarker, item.Setting, item.Target, item.Original, item.CheckSet);

        private static bool IsPutBack(string line) => Parse(line) != null;

        private static CheckPutBack? Parse(string line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 6 || parts[1] != PutBackMarker || parts[2].Length == 0) return null;
            return new CheckPutBack(parts[2], parts[3], parts[4], parts[5]);
        }

        private static List<CheckPutBack> PutBacks(List<string> lines, string serial) =>
            lines.Where(l => Same(l, serial)).Select(Parse).Where(p => p != null).Select(p => p!).ToList();

        private static bool Same(string line, string serial) =>
            string.Equals(line.Split(' ', '\t')[0], serial, StringComparison.OrdinalIgnoreCase);

        private static (string Serial, string Reason)? Find(List<string> lines, string serial)
        {
            string? line = lines.FirstOrDefault(l => Same(l, serial) && !IsPutBack(l));
            if (line == null) return null;
            int space = line.IndexOf(' ');
            return (serial, space < 0 ? "withheld" : line[(space + 1)..].Trim());
        }

        private List<string> Read()
        {
            try
            {
                if (!File.Exists(_path)) return new List<string>();
                return File.ReadAllLines(_path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CheckRestoreReminderStore: could not read " + _path + ": " + ex.Message, TraceLevel.Warning);
                return new List<string>();
            }
        }

        private void Write(List<string> serials)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (serials.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
                File.WriteAllLines(_path, serials);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CheckRestoreReminderStore: could not write " + _path + ": " + ex.Message, TraceLevel.Warning);
            }
        }
    }
}
