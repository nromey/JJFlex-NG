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

        /// <summary>Why a reminder is owed to this radio ("withheld" or "unconfirmed"), or null when none is.</summary>
        public string? ReasonFor(string? serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            lock (Gate) return Find(Read(), serial.Trim())?.Reason;
        }

        public bool Contains(string? serial) => ReasonFor(serial) != null;

        /// <summary>Owe this radio a reminder. A later reason replaces an earlier one.</summary>
        public void Add(string? serial, string reason)
        {
            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(reason)) return;
            lock (Gate)
            {
                var lines = Read();
                lines.RemoveAll(l => Same(l, serial.Trim()));
                lines.Add(serial.Trim() + " " + reason.Trim());
                Write(lines);
            }
        }

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

        private static bool Same(string line, string serial) =>
            string.Equals(line.Split(' ')[0], serial, StringComparison.OrdinalIgnoreCase);

        private static (string Serial, string Reason)? Find(List<string> lines, string serial)
        {
            string? line = lines.FirstOrDefault(l => Same(l, serial));
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
