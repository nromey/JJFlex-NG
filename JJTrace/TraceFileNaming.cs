using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace JJTrace
{
    /// <summary>
    /// Where a detached trace file goes, and what it is called.
    ///
    /// <para>One renderer for three callers that must agree: rotation naming a
    /// closed part, the coordinator detaching an archived session, and boot
    /// maintenance adopting a chain a killed run left behind. The names are
    /// what the leftover sweep matches on, so a disagreement here is a leftover
    /// file nobody ever adopts.</para>
    /// </summary>
    public static class TraceFileNaming
    {
        /// <summary>
        /// <c>&lt;stem&gt;-&lt;boot stamp&gt;.txt</c>, collision-safe. The shape
        /// the plain-text retention sweep looks for.
        /// </summary>
        public static string StampedPath(string livePath, DateTime bootTimeUtc)
        {
            return Unique(livePath, (dir, baseName, ext, stamp, suffix) =>
                Path.Combine(dir, string.Format(CultureInfo.InvariantCulture,
                    suffix == 0 ? "{0}-{1:yyyyMMdd-HHmmss}{2}" : "{0}-{1:yyyyMMdd-HHmmss}-{3}{2}",
                    baseName, stamp, ext, suffix)), bootTimeUtc);
        }

        /// <summary>
        /// <c>&lt;stem&gt;-&lt;boot stamp&gt;-part-NNN.txt</c>, collision-safe.
        /// Parts of one session share a stem and zero-pad to three digits so
        /// part 100 still sorts after part 099.
        /// </summary>
        public static string StampedPartPath(string livePath, DateTime bootTimeUtc, int partNumber)
        {
            return Unique(livePath, (dir, baseName, ext, stamp, suffix) =>
                Path.Combine(dir, string.Format(CultureInfo.InvariantCulture,
                    suffix == 0 ? "{0}-{1:yyyyMMdd-HHmmss}-part-{3:D3}{2}"
                                : "{0}-{1:yyyyMMdd-HHmmss}-part-{3:D3}-{4}{2}",
                    baseName, stamp, ext, partNumber, suffix)), bootTimeUtc);
        }

        private static string Unique(string livePath,
                                     Func<string, string, string, DateTime, int, string> build,
                                     DateTime bootTimeUtc)
        {
            if (string.IsNullOrEmpty(livePath)) return null;
            string dir = Path.GetDirectoryName(livePath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(livePath);
            string ext = Path.GetExtension(livePath);
            if (string.IsNullOrEmpty(ext)) ext = ".txt";
            DateTime stamp = bootTimeUtc.Kind == DateTimeKind.Utc ? bootTimeUtc.ToLocalTime() : bootTimeUtc;

            string target = build(dir, baseName, ext, stamp, 0);
            int suffix = 1;
            while (File.Exists(target))
            {
                target = build(dir, baseName, ext, stamp, suffix);
                suffix++;
            }
            return target;
        }

        /// <summary>
        /// Move a closed trace file out of the live path.
        ///
        /// <para><b>It does not delete on failure, and that is the change.</b>
        /// The old helper deleted the source after a failed rename so the next
        /// session was not blocked from opening a clean file — reasonable when
        /// it ran AFTER a successful archive, and unsuitable here, where the
        /// move happens BEFORE anything has been compressed. Deleting would
        /// destroy the only copy of the evidence to tidy a path. Callers that
        /// really are past a successful archive pass
        /// <paramref name="deleteOnFailure"/>.</para>
        /// </summary>
        /// <returns>The new path, or null if the move failed.</returns>
        public static string Detach(string sourcePath, string targetPath,
                                    bool deleteOnFailure, out string failure)
        {
            failure = null;
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetPath)) return null;
            try
            {
                File.Move(sourcePath, targetPath);
                return targetPath;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                if (deleteOnFailure)
                {
                    try { File.Delete(sourcePath); } catch { }
                }
                return null;
            }
        }
    }

    /// <summary>
    /// Paths that must survive the housekeeping sweeps until whoever selected
    /// them is finished.
    ///
    /// <para>The problem-report bundler is the reason: it now freezes a part of
    /// a LIVE session and then spends a while writing an outer zip around it.
    /// The plain-text retention sweep and the archive prune both run on their
    /// own schedules and neither has any idea that a bundle is in flight. A pin
    /// is how the bundle says "not this one, not yet".</para>
    /// </summary>
    public static class TraceEvidencePins
    {
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, int> _pins =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Pin a full path. Reference-counted: two bundles can pin the
        /// same evidence and the second release is what frees it.</summary>
        public static void Pin(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            lock (_sync)
            {
                _pins.TryGetValue(fullPath, out int n);
                _pins[fullPath] = n + 1;
            }
        }

        public static void Release(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            lock (_sync)
            {
                if (!_pins.TryGetValue(fullPath, out int n)) return;
                if (n <= 1) _pins.Remove(fullPath);
                else _pins[fullPath] = n - 1;
            }
        }

        public static bool IsPinned(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            lock (_sync) { return _pins.ContainsKey(fullPath); }
        }

        /// <summary>Tests only — the pin table is process-global.</summary>
        internal static void ClearForTests()
        {
            lock (_sync) { _pins.Clear(); }
        }
    }
}
