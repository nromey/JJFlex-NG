using System;
using System.Globalization;

namespace Radios;

/// <summary>The answer to which Audio Check take Play last take may use.</summary>
public enum TakePlaybackChoice { None, Slice, OffAir, Computer, UnknownTime }

/// <summary>Pure playback choice; a missing timestamp cannot win a comparison.</summary>
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

    public static TakePlaybackChoice Choose(AudioCheckListenMethods method,
        bool hasSlice, DateTime? sliceAt, bool hasIq, DateTime? iqAt,
        bool hasComputer, DateTime? computerAt)
    {
        if (method == AudioCheckListenMethods.RecordPlayback)
            return hasSlice ? TakePlaybackChoice.Slice : TakePlaybackChoice.None;
        if (method == AudioCheckListenMethods.OffAir)
            return hasIq ? TakePlaybackChoice.OffAir : TakePlaybackChoice.None;

        int count = (hasSlice ? 1 : 0) + (hasIq ? 1 : 0) + (hasComputer ? 1 : 0);
        if (count == 0) return TakePlaybackChoice.None;
        if (count == 1)
            return hasSlice ? TakePlaybackChoice.Slice : hasIq ? TakePlaybackChoice.OffAir : TakePlaybackChoice.Computer;
        if (hasSlice && sliceAt == null || hasIq && iqAt == null || hasComputer && computerAt == null)
            return TakePlaybackChoice.UnknownTime;

        // All present kinds have comparable times. Ties use the same order
        // each time so a repeated press never quietly switches sources.
        DateTime latest = DateTime.MinValue;
        TakePlaybackChoice choice = TakePlaybackChoice.None;
        if (hasSlice && sliceAt!.Value >= latest) { latest = sliceAt.Value; choice = TakePlaybackChoice.Slice; }
        if (hasComputer && computerAt!.Value >= latest) { latest = computerAt.Value; choice = TakePlaybackChoice.Computer; }
        if (hasIq && iqAt!.Value >= latest) choice = TakePlaybackChoice.OffAir;
        return choice;
    }
}
