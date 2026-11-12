using System;
using System.IO;
using System.Text;

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
    /// <para><b>It changes what the disk holds, never what a caller sees.</b>
    /// Every failure is thrown to the caller exactly as the old
    /// truncate-then-write threw it, after the temporary file has been removed,
    /// so a store that swallowed and traced still does, a store that returned
    /// false still does, and a store that re-threw still does.</para>
    /// <para>The temporary file is the target's name plus <see cref="TempSuffix"/>
    /// in the same folder: the same volume, so the replace is a rename and not a
    /// copy, and one name per store, so a temporary file a crash left behind is
    /// simply overwritten by the next save.</para>
    /// </remarks>
    public static class AtomicFile
    {
        /// <summary>What is appended to the target's name for the temporary file.</summary>
        public const string TempSuffix = ".tmp";

        /// <summary>The temporary file a write to <paramref name="path"/> uses.</summary>
        public static string TempPathFor(string path) => path + TempSuffix;

        /// <summary>
        /// Write <paramref name="path"/> through <paramref name="write"/>, which
        /// is handed a stream on the temporary file. The real file is replaced
        /// only once <paramref name="write"/> has returned and the stream is
        /// flushed to the disk.
        /// </summary>
        public static void Write(string path, Action<Stream> write)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("No file to write.", nameof(path));
            if (write == null) throw new ArgumentNullException(nameof(write));

            string temp = TempPathFor(path);
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    write(fs);
                    fs.Flush(flushToDisk: true);
                }
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

        private static void TryDelete(string temp)
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // The write's own failure is the one the caller needs; a
                // temporary file that cannot be removed is overwritten by the
                // next save.
            }
        }
    }
}
