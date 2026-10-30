#nullable enable
using System;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// What keyed the transmitter, read from the radio's own interlock status
    /// line rather than from FlexLib's parse of it (#835, bench 2026-10-28).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why FlexLib's answer is not enough.</b> With the app's software key up
    /// and a hardware line pressed as well, the radio reports BOTH sources in
    /// one field: <c>interlock ... source=RCA,SW</c>. FlexLib's
    /// <c>Radio.ParsePTTSource</c> knows single words only, returns
    /// <c>PTTSource.None</c> for the list, and the parser then skips the field
    /// (<c>continue</c>), so <c>Radio.PTTSource</c> keeps whatever it held
    /// before, which during a box send is <c>SW</c>. Read off the 8600 on
    /// 2026-10-28: a microphone PTT pressed during a box send never took it
    /// over, because nothing above FlexLib could see the hardware line.
    /// </para>
    /// <para>
    /// The raw field is the radio's word; this class only reads it. A list is
    /// hardware when ANY member is a hardware line, because a hardware line
    /// holds the carrier whatever else is keying, and a software unkey cannot
    /// drop it.
    /// </para>
    /// </remarks>
    public static class PttSourceReading
    {
        /// <summary>
        /// True when a hardware line (the microphone's, ACC or the rear RCA)
        /// is keying the transmitter. <paramref name="rawSource"/> is the
        /// radio's own last <c>source=</c> value, or null when none has been
        /// read, in which case FlexLib's parse is the answer.
        /// </summary>
        public static bool IsHardware(PTTSource parsed, string? rawSource)
        {
            if (rawSource == null)
                return parsed is PTTSource.Mic or PTTSource.ACC or PTTSource.RCA;
            foreach (string part in rawSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(part, "MIC", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "ACC", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "RCA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The <c>source=</c> value of a raw interlock status line, exactly as
        /// the radio sent it, or null when the line is not an interlock status
        /// or carries no source. An empty value is returned as empty: the radio
        /// saying "no source" is an answer, not a missing one.
        /// </summary>
        /// <param name="line">A raw status line, <c>S&lt;handle&gt;|interlock state=... source=...</c>.</param>
        public static string? SourceFromInterlockStatus(string? line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != 'S') return null;
            int bar = line.IndexOf('|');
            if (bar < 0) return null;
            string body = line.Substring(bar + 1);
            if (!body.StartsWith("interlock ", StringComparison.Ordinal)) return null;
            foreach (string token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("source=", StringComparison.Ordinal))
                    return token.Substring("source=".Length);
            }
            return null;
        }
    }
}
