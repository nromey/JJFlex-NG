#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The file that says a once-only carry-over at the first start after an
    /// upgrade has been made, and to whom (#1106).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a marker, and why written last.</b> Ruled by Noel 2026-11-12
    /// (J1): only operators that existed at the upgrade start from the Windows
    /// account's choices; an operator added later starts with none. The
    /// carry-over is made for every operator listed at that first start, and
    /// then this file is written. Its existence is the whole answer to "has the
    /// upgrade happened"; the list inside is for a person reading the folder,
    /// not for the code. Written after the copies, so a start that stops part
    /// way repeats the carry-over (each copy is skipped for an operator whose
    /// file is already there) rather than leaving someone out.</para>
    /// <para>Plain JSON beside the stores it describes, so support can read it
    /// and deleting it is the documented way to make the carry-over run again
    /// at the next start.</para>
    /// </remarks>
    internal static class UpgradeMarker
    {
        internal static void Write(string path, IReadOnlyCollection<string> operators, string what)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var payload = new
                {
                    version = 1,
                    when = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    operators = operators.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
                };
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Not written: the next start makes the carry-over again, which
                // copies only to operators who still have no file. Said, so the
                // trace explains it.
                Tracing.TraceLine(what + ": could not write the upgrade marker " + path + ": " + ex.Message,
                    System.Diagnostics.TraceLevel.Warning);
            }
        }
    }
}
