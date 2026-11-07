using System;
using System.Globalization;

namespace Radios;

/// <summary>The answer to which Audio Check take Play last take may use.</summary>
/// <summary>What Play last take plays: the radio's slot, an off-air take, or a saved playback take (Unified takes).</summary>
public enum TakePlaybackChoice { None, Slice, OffAir, Saved }

/// <summary>Only a take-making listen method offers playback.</summary>
public static class TakePlaybackSelector
{
    public static string PlayLabel(DateTime? madeUtc, DateTime nowLocal)
    {
        if (madeUtc == null || madeUtc == default ||
            nowLocal - madeUtc.Value.ToLocalTime() <= TimeSpan.FromHours(1))
            return "Play last take";

        DateTime madeLocal = madeUtc.Value.ToLocalTime();
        return madeLocal.Date == nowLocal.Date
            ? "Play " + madeLocal.ToString("h:mm tt", CultureInfo.InvariantCulture) + " take"
            : "Play " + madeLocal.ToString("MMMM d", CultureInfo.InvariantCulture) + " take";
    }

    /// <param name="hasSaved">This check's saved playback take of the method exists on disk (Unified takes, part 4d).
    /// It is preferred, and the radio's slot is the fallback, as the design says.</param>
    public static TakePlaybackChoice Choose(AudioCheckListenMethods method, bool hasSlice, bool hasIq, bool hasSaved = false)
    {
        if (method == AudioCheckListenMethods.RecordPlayback)
            return hasSaved ? TakePlaybackChoice.Saved : hasSlice ? TakePlaybackChoice.Slice : TakePlaybackChoice.None;
        if (method == AudioCheckListenMethods.Loopback)
            return hasSaved ? TakePlaybackChoice.Saved : TakePlaybackChoice.None;
        if (method == AudioCheckListenMethods.OffAir)
            return hasIq ? TakePlaybackChoice.OffAir : TakePlaybackChoice.None;
        return TakePlaybackChoice.None;
    }
}
