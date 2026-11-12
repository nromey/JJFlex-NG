using System;
using System.IO;

namespace Radios
{
    /// <summary>
    /// A settings file that could not be read is set aside under a dated name
    /// beside it, never written over: the store starts from its defaults, and
    /// the operator's data is still on the disk for whoever comes to look.
    /// </summary>
    /// <remarks>
    /// The filter presets did this first (#49 family); the pan adapter's
    /// operator file does it too. One naming, so a person looking for the
    /// file finds the same shape whichever store set it aside.
    /// </remarks>
    public static class UnreadableSettingsFile
    {
        /// <summary>The name an unreadable <paramref name="path"/> is kept under, read at <paramref name="when"/>.</summary>
        public static string SetAsideName(string path, DateTime when) =>
            path + $".unreadable-{when:yyyyMMdd-HHmmss}";

        /// <summary>
        /// Move <paramref name="path"/> aside. Returns where it now is, or
        /// <paramref name="path"/> itself when it could not be moved (the
        /// failure is traced).
        /// </summary>
        public static string SetAside(string path)
        {
            string kept = SetAsideName(path, DateTime.Now);
            try
            {
                File.Move(path, kept);
                return kept;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Settings: could not set aside the unreadable {Path.GetFileName(path)}: {ex.Message}");
                return path;
            }
        }
    }
}
