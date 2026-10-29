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
    /// The radios whose transmit monitor a send turned on and could not put
    /// back, because the connection went first (#835, Codex rounds 1 and 2 on
    /// the Track B fix round). One serial per line in a small file under the
    /// settings root, so the record survives what an ordinary close and
    /// reopen does: the FlexBase is disposed and a new one is made.
    /// </summary>
    /// <remarks>
    /// A record is cleared only when the radio acknowledges the put-back
    /// (<see cref="FlexBase.PutBackMonitorNow"/>). A read or write failure is
    /// traced and reads as "nothing recorded": this is a courtesy restore of
    /// a monitor setting, and must never stop a connect.
    /// </remarks>
    public sealed class SendMonitorPutBackStore
    {
        private const string FileName = "sendMonitorPutBack.txt";
        private static readonly object Gate = new();
        private readonly string _path;

        /// <summary>A store in <paramref name="directory"/>; production passes <see cref="RadioConfig.AppDataRoot"/>.</summary>
        public SendMonitorPutBackStore(string directory)
        {
            _path = Path.Combine(directory ?? throw new ArgumentNullException(nameof(directory)), FileName);
        }

        /// <summary>True when a put-back is owed to the radio with this serial.</summary>
        public bool Contains(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            lock (Gate) return Read().Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Record that a put-back is owed to this radio.</summary>
        public void Add(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            lock (Gate)
            {
                var serials = Read();
                if (serials.Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase)) return;
                serials.Add(serial.Trim());
                Write(serials);
            }
        }

        /// <summary>The put-back was made; forget it.</summary>
        public void Remove(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            lock (Gate)
            {
                var serials = Read();
                if (serials.RemoveAll(s => string.Equals(s, serial.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
                    return;
                Write(serials);
            }
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
                Tracing.TraceLine("SendMonitorPutBackStore: could not read " + _path + ": " + ex.Message, TraceLevel.Warning);
                return new List<string>();
            }
        }

        private void Write(List<string> serials)
        {
            try
            {
                if (serials.Count == 0)
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllLines(temp, serials);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SendMonitorPutBackStore: could not write " + _path + ": " + ex.Message, TraceLevel.Warning);
            }
        }
    }
}
