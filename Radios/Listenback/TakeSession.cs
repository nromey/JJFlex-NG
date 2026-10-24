using System;
using System.Globalization;
using System.IO;

namespace Radios.Listenback;

/// <summary>
/// Which takes belong to this session, meaning this run of the app (Noel,
/// 2026-10-22 07:10, round 14 bench): the automatic comparison only ever
/// compares against a take made in this session, and the first take of a
/// session gets no comparison. "Compare the last two IQ takes" may still reach
/// an older take, and then says which session it came from. A take made last
/// night was made under conditions nobody remembers, so numbers against it
/// mislead more than they inform; a second take is cheap.
/// </summary>
public static class TakeSession
{
    private const string NameStamp = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// When a take was made, in UTC. The take directory's name is stamped at
    /// creation (<c>20261022-120621-258-bc8dd8e6</c>) and never changes, so it
    /// is read first; take.txt's write time is the fallback for a directory
    /// named some other way.
    /// </summary>
    public static DateTime MadeUtc(string takeDirectory)
    {
        string name = Path.GetFileName(takeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.Length >= NameStamp.Length &&
            DateTime.TryParseExact(name.Substring(0, NameStamp.Length), NameStamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime stamped))
            return stamped;
        return File.GetLastWriteTimeUtc(Path.Combine(takeDirectory, "take.txt"));
    }

    /// <summary>True when the take was made at or after this session began.</summary>
    public static bool IsThisSession(string takeDirectory, DateTime sessionStartUtc) =>
        MadeUtc(takeDirectory) >= sessionStartUtc;

    /// <summary>
    /// The sentence that goes in front of a manual comparison whose older take
    /// is from an earlier session, e.g. "Your last take is from an earlier
    /// session, yesterday at 8:15 PM." Both times are local.
    /// </summary>
    public static string EarlierSessionNote(DateTime takeLocal, DateTime nowLocal)
    {
        string time = takeLocal.ToString("h:mm tt", CultureInfo.CurrentCulture);
        string when = takeLocal.Date == nowLocal.Date ? Lexicon.Get("audio.iq.when_today", ("time", time))
            : takeLocal.Date == nowLocal.Date.AddDays(-1) ? Lexicon.Get("audio.iq.when_yesterday", ("time", time))
            : Lexicon.Get("audio.iq.when_date", ("date", takeLocal.ToString("dddd, MMMM d", CultureInfo.CurrentCulture)),
                ("time", time));
        return Lexicon.Get("audio.iq.earlier_session", ("when", when));
    }
}
