using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Writes a settings file whole or not at all. The new contents go to a
    /// temporary file beside the real one, are flushed to the disk, and then
    /// replace the real file in one step, so a failure part-way through (an
    /// exception in a serializer, a full disk, the process ending) leaves the
    /// previous file exactly as it was rather than an empty or half-written one
    /// that loads as defaults.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape <c>AdvisorySuppression</c> already used; the
    /// operator settings stores share it through here.</para>
    /// <para><b>It changes what the disk holds, never whether a caller sees a
    /// failure.</b> Every failure is thrown to the caller after this write's
    /// temporary file has been removed, so a store that swallowed and traced
    /// still does, a store that returned false still does, and a store that
    /// re-threw still does. The exception's type can differ from the old
    /// truncate-then-write's: a target another program holds open is refused
    /// at the replace, which Windows reports as access denied.</para>
    /// <para><b>Each write has its own temporary file</b>: the target's name, a
    /// dot, a fresh GUID and <see cref="TempSuffix"/>, in the same folder (the
    /// same volume, so the replace is a rename and not a copy), opened with
    /// <see cref="FileMode.CreateNew"/>. Two saves of one file at once therefore
    /// never share a temporary file: neither can truncate the other's pending
    /// contents, and a write that fails removes only its own. The last one to
    /// replace the file wins, and the file is whole either way.</para>
    /// <para><b>A temporary file a crash left behind</b> is removed by a later
    /// save of the same file once it is older than <see cref="LeftoverAge"/>,
    /// and the removal is traced. A younger one is left alone, because it may
    /// be another save's, still in flight. So leftovers cannot pile up: there is
    /// at most one per crash, and each goes at the first save after it ages.</para>
    /// </remarks>
    public static class AtomicFile
    {
        /// <summary>What every temporary file's name ends with.</summary>
        public const string TempSuffix = ".tmp";

        /// <summary>
        /// How old a temporary file must be before a later save treats it as a
        /// crash's leftover and removes it. A real save holds its temporary file
        /// for milliseconds; this is far past that.
        /// </summary>
        public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Runs in a write after its temporary file is closed and before it
        /// replaces the real file, on the writing thread only. For tests that
        /// need to land a second save in exactly that window.
        /// </summary>
        [ThreadStatic] internal static Action<string> BeforeReplaceForTests;

        /// <summary>A fresh temporary file name for one write to <paramref name="path"/>.</summary>
        public static string NewTempPathFor(string path) =>
            path + "." + Guid.NewGuid().ToString("N") + TempSuffix;

        /// <summary>
        /// The temporary files beside <paramref name="path"/> that writes to it
        /// made and have not (yet) removed or renamed.
        /// </summary>
        public static IReadOnlyList<string> TempFilesFor(string path)
        {
            var found = new List<string>();
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir == null || !Directory.Exists(dir)) return found;
            string name = Path.GetFileName(path);
            var ours = new Regex("^" + Regex.Escape(name) + @"\.[0-9a-f]{32}" + Regex.Escape(TempSuffix) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (string f in Directory.EnumerateFiles(dir, name + ".*"))
            {
                if (ours.IsMatch(Path.GetFileName(f))) found.Add(f);
            }
            return found;
        }

        /// <summary>
        /// Write <paramref name="path"/> through <paramref name="write"/>, which
        /// is handed a stream on this write's own temporary file. The real file
        /// is replaced only once <paramref name="write"/> has returned and the
        /// stream is flushed to the disk.
        /// </summary>
        public static void Write(string path, Action<Stream> write)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("No file to write.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            RemoveLeftovers(path);

            string temp = NewTempPathFor(path);
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(fs);
                    fs.Flush(flushToDisk: true);
                }
                BeforeReplaceForTests?.Invoke(path);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }

        /// <summary>
        /// Write <paramref name="text"/> to <paramref name="path"/> as UTF-8
        /// without a byte order mark, which is what <c>File.WriteAllText</c>
        /// writes.
        /// </summary>
        public static void WriteAllText(string path, string text)
        {
            Write(path, s =>
            {
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text ?? string.Empty);
                s.Write(bytes, 0, bytes.Length);
            });
        }

        /// <summary>
        /// Remove the temporary files beside <paramref name="path"/> that are
        /// older than <see cref="LeftoverAge"/>: a crash left them, since a live
        /// save holds its own for milliseconds. Never fails the save.
        /// </summary>
        private static void RemoveLeftovers(string path)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - LeftoverAge;
                foreach (string f in TempFilesFor(path))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                        File.Delete(f);
                        Tracing.TraceLine("AtomicFile: removed a temporary file an earlier save left behind: " + f,
                            System.Diagnostics.TraceLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("AtomicFile: could not remove the leftover temporary file " + f + ": " + ex.Message,
                            System.Diagnostics.TraceLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AtomicFile: could not look for leftover temporary files beside " + path + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        private static void TryDelete(string temp)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // The write's own failure is the one the caller needs. A
                // temporary file that cannot be removed now is removed by a
                // later save once it is older than LeftoverAge.
            }
        }
    }
}
