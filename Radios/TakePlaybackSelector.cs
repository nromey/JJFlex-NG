using System;
using System.Globalization;

namespace Radios;

/// <summary>The answer to which Audio Check take Play last take may use.</summary>
public enum TakePlaybackChoice { None, Slice, OffAir }

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

    public static TakePlaybackChoice Choose(AudioCheckListenMethods method, bool hasSlice, bool hasIq)
    {
        if (method == AudioCheckListenMethods.RecordPlayback)
            return hasSlice ? TakePlaybackChoice.Slice : TakePlaybackChoice.None;
        if (method == AudioCheckListenMethods.OffAir)
            return hasIq ? TakePlaybackChoice.OffAir : TakePlaybackChoice.None;
        return TakePlaybackChoice.None;
    }
}
