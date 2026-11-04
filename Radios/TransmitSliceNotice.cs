using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// What a door does when it keys a transmit slice that is not the
    /// selected one (#863). Ruled by Noel 2026-11-03: three choices, speak and
    /// tone the default. Stored as an int in the operator's audio settings.
    /// </summary>
    public enum TransmitElsewhereNotice
    {
        /// <summary>The sentence, once per split, and the tri-tones at every key. The default.</summary>
        SpeakAndTone = 0,

        /// <summary>The tri-tones only.</summary>
        ToneOnly = 1,

        /// <summary>Neither. Check your audio still speaks it (see <see cref="TransmitSliceNotice"/>).</summary>
        Off = 2,
    }

    /// <summary>
    /// Before a door keys the transmit slice while the operator is on another,
    /// one sentence naming the slice and frequency it will transmit on, then
    /// the transmission (#863). Built once, used by every door that keys from a
    /// dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-11-02 17:36: "Say it, then go".</b> On 2026-11-02 a
    /// 1 W check keyed slice A on 14.100, the international beacon frequency,
    /// for about six seconds while slice B had been the slice listened to, and
    /// nothing said where. And for the other doors, <i>"Yes, all of them"</i>.
    /// </para>
    /// <para>
    /// <b>The words are #642's</b>, "Frequency 14.100, slice A, transmit": the
    /// same sentence F says for the transmit slice, so the operator meets one
    /// phrasing for this fact wherever it is said. Silent when the transmit
    /// slice IS the selected one, which is the common case and the safe one.
    /// </para>
    /// <para>
    /// <b>Then go means after it has been heard.</b> The sentence goes out on
    /// the countdown's own speech channel (<see cref="ScreenReaderOutput.CountdownSpeech"/>),
    /// queued and followed, so a door can wait for the reader to finish it
    /// before it keys; a check's countdown, when it is on, is queued behind it
    /// and so comes after it. A sentence the reader never got, or that does
    /// not finish within <see cref="CeilingMs"/>, is not heard, and the door
    /// keys nothing.
    /// </para>
    /// </remarks>
    public static class TransmitSliceNotice
    {
        /// <summary>The operator's choice, pushed here from the audio settings.</summary>
        public static TransmitElsewhereNotice Setting { get; set; } = TransmitElsewhereNotice.SpeakAndTone;

        /// <summary>The longest a door waits for the sentence: the countdown's own ceiling.</summary>
        public const int CeilingMs = CountdownRun.SpeechCeilingMs;

        /// <summary>How often a door asks again when the reader gives no news.</summary>
        public const int PollMs = CountdownRun.SpeechPollMs;

        /// <summary>
        /// The sentence, or null when there is nothing to say: the transmit
        /// slice is the selected one, or there is no transmit slice. Pure.
        /// DRAFT PROSE (#642, #863), awaiting Noel.
        /// </summary>
        public static string Sentence(SliceRoles roles, Func<ulong, string> format)
        {
            if (!roles.TransmitsElsewhere) return null;
            if (format == null) throw new ArgumentNullException(nameof(format));
            return Lexicon.Get("connect.home.frequency_transmit",
                ("freqText", format(roles.TransmitHz)), ("slice", roles.TransmitLetter));
        }

        /// <summary>The sentence for this rig now, or null.</summary>
        public static string For(FlexBase rig)
        {
            if (rig == null) return null;
            try { return Sentence(SliceRoles.Of(rig), FormatterFor(rig)); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice: could not read the slices: " + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// Say the sentence for this rig, if there is one, and return its
        /// handle to wait on; null when there is nothing to say.
        /// </summary>
        /// <param name="rig">The radio about to be keyed.</param>
        /// <param name="door">Which door, for the trace.</param>
        public static ICountdownWords Say(FlexBase rig, string door)
        {
            string sentence = For(rig);
            if (sentence == null) return null;
            Tracing.TraceLine("TransmitSliceNotice (" + door + "): the transmit slice is not the selected one; saying \""
                + sentence + "\" before keying", TraceLevel.Info);
            try { return ScreenReaderOutput.CountdownSpeech.Say(sentence, onlyWarning: true); }
            catch (Exception ex)
            {
                Tracing.TraceLine("TransmitSliceNotice (" + door + "): could not be said: " + ex.Message, TraceLevel.Warning);
                return CountdownWords.Undelivered;
            }
        }

        /// <summary>
        /// Wait until the reader has finished <paramref name="words"/>. True:
        /// heard, go. False: it was never delivered, it ran past the ceiling,
        /// or <paramref name="stillWanted"/> went false; do not key. Null words
        /// (nothing to say) are true at once.
        /// </summary>
        /// <param name="words">The handle <see cref="Say"/> returned, or null.</param>
        /// <param name="stillWanted">Asked on every poll: false once the door has been cancelled.</param>
        /// <param name="delay">The wait between polls; a test passes its own.</param>
        /// <param name="ceilingMs">The longest to wait.</param>
        public static async Task<bool> HeardAsync(ICountdownWords words, Func<bool> stillWanted,
            Func<int, Task> delay = null, int ceilingMs = CeilingMs)
        {
            if (words == null) return true;
            delay ??= Task.Delay;
            int waited = 0;
            while (true)
            {
                if (stillWanted != null && !stillWanted()) return false;
                if (words.Failed) return false;
                Task news = words.NextNews;
                if (words.Finished) return true;
                if (waited >= ceilingMs) return false;
                await Task.WhenAny(news, delay(PollMs)).ConfigureAwait(true);
                waited += PollMs;
            }
        }

        private static Func<ulong, string> FormatterFor(FlexBase rig)
        {
            var format = rig.Callouts?.FormatFreq;
            if (format != null) return hz => format(hz);
            return hz => (hz / 1_000_000.0).ToString("0.000###", CultureInfo.InvariantCulture);
        }
    }
}
