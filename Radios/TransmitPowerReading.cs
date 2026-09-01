using System;

namespace Radios
{
    /// <summary>
    /// Forward and reflected transmit power as ONE reading, carrying the skew
    /// between the two meter samples it was built from and the age of the
    /// newer of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists at all (#453).</b> Forward and reflected power
    /// arrive as two separate FlexLib meter callbacks, each assigning its own
    /// field with no timestamp. Every consumer used to do this:
    /// </para>
    /// <code>
    /// forward   = rig.ForwardPowerWatts;
    /// reflected = rig.ReflectedPowerWatts;
    /// </code>
    /// <para>
    /// Two back-to-back property gets are not a sample of one instant. Each
    /// field holds whatever its own last callback deposited, and the ratio of
    /// two readings taken at different moments is not a reflected share — it
    /// is an artefact.
    /// </para>
    /// <para>
    /// <b>On voice it is not a subtle artefact.</b> On SSB the forward power
    /// follows the speech envelope; between syllables it plunges toward zero
    /// and climbs back many times a second. Reflected power follows its own
    /// envelope through its own callback. Any skew at all puts a small forward
    /// reading underneath a larger, slightly older reflected one and the ratio
    /// spikes past any threshold you care to set. A TUNE is a steady carrier,
    /// so there is no envelope and no skew — which is exactly why an operator's
    /// tuner reads 1.5 and is right, while the alarm fires only once he starts
    /// talking and ends the transmission on a correctly matched antenna.
    /// </para>
    /// <para>
    /// <b>It is deliberately not fixed by smoothing.</b> Averaging a bad pair
    /// in with good ones lowers the spike AND delays a real alarm, which is the
    /// wrong trade on a protective feature. The fix is to refuse to judge a
    /// pair that was not sampled together.
    /// </para>
    /// <para>
    /// <b>What "sampled together" means here.</b> FlexLib drains its meter
    /// queue into one dictionary and then dispatches every meter in that drain
    /// back to back on one thread (Radio's meter-packet thread function),
    /// so forward and reflected normally land microseconds apart. A burst is
    /// roughly a tenth of a second, so a skew anywhere near
    /// <see cref="MaxSkewMilliseconds"/> means the two came from DIFFERENT
    /// bursts and the pair must not be judged.
    /// </para>
    /// </remarks>
    public readonly struct TransmitPowerReading
    {
        /// <summary>
        /// No reading — one or both meters have never reported. Judges nothing
        /// and is what an unreadable radio yields, so an absent meter can never
        /// be mistaken for a healthy station or a fault.
        /// </summary>
        public static readonly TransmitPowerReading None = default;

        /// <summary>
        /// The most skew, in milliseconds, that still counts as one sample.
        /// <para>Tight on purpose. The two meters arrive in the same dispatch
        /// burst microseconds apart, and a burst period is around a tenth of a
        /// second, so this sits an order of magnitude above the normal case and
        /// well below "a different burst". A speech envelope moves in tens of
        /// milliseconds, so anything looser is not a pair.</para>
        /// </summary>
        public const float MaxSkewMilliseconds = 60f;

        /// <summary>
        /// The oldest a reading may be and still be judged.
        /// <para>Without this a transmission that stops producing meter data —
        /// a dropped remote link, a radio that went away — leaves both fields
        /// frozen at their last values, and every consumer keeps judging a
        /// photograph. Generous relative to the few-times-a-second meter rate,
        /// so an ordinary scheduling hiccup does not silence the alarm.</para>
        /// </summary>
        public const float MaxAgeMilliseconds = 1500f;

        private readonly bool _hasBothMeters;

        public TransmitPowerReading(float forwardWatts, float reflectedWatts,
                                    float skewMilliseconds, float ageMilliseconds)
        {
            ForwardWatts = forwardWatts;
            ReflectedWatts = reflectedWatts;
            SkewMilliseconds = skewMilliseconds;
            AgeMilliseconds = ageMilliseconds;
            _hasBothMeters = true;
        }

        /// <summary>True when both meters have reported at least once.</summary>
        public bool HasBothMeters => _hasBothMeters;

        /// <summary>Forward power in WATTS, not dBm.</summary>
        public float ForwardWatts { get; }

        /// <summary>Reflected power in WATTS, not dBm.</summary>
        public float ReflectedWatts { get; }

        /// <summary>
        /// Milliseconds between the two meter samples this reading was built
        /// from. Near zero when they came from one dispatch burst.
        /// </summary>
        public float SkewMilliseconds { get; }

        /// <summary>
        /// Milliseconds since the NEWER of the two samples arrived.
        /// </summary>
        public float AgeMilliseconds { get; }

        /// <summary>
        /// Whether this is one sample of one instant, and therefore something a
        /// share may honestly be computed from.
        /// </summary>
        public bool IsCoherent =>
            _hasBothMeters
            && !float.IsNaN(ForwardWatts) && !float.IsNaN(ReflectedWatts)
            && SkewMilliseconds <= MaxSkewMilliseconds
            && AgeMilliseconds <= MaxAgeMilliseconds;

        /// <summary>
        /// The share of forward power coming back, 0 to 1 — or NaN when the
        /// question cannot honestly be answered, INCLUDING when the two
        /// readings were not sampled together.
        /// </summary>
        /// <remarks>
        /// NaN rather than a comfortable number for "no idea" is the rule this
        /// whole area exists to enforce: the radio's own SWR meter answers
        /// 1.008 when it has nothing useful to say, and two bench sessions were
        /// measured straight through that reassurance.
        /// </remarks>
        public float ReflectedShare =>
            IsCoherent
                ? TransmitSafety.ReflectedFractionOf(ForwardWatts, ReflectedWatts)
                : float.NaN;

        /// <summary>
        /// Why this reading cannot be judged, in a few words, for a trace. An
        /// empty string when it can be.
        /// </summary>
        /// <remarks>
        /// Exists because a guard that silently declines to judge is
        /// indistinguishable from a guard that is watching and seeing nothing
        /// wrong. If a radio ever delivers the two meters in separate bursts,
        /// the alarm would simply stop working and nothing would say so — this
        /// is what lets a bench sitting tell the difference.
        /// </remarks>
        public string WhyNotCoherent
        {
            get
            {
                if (!_hasBothMeters) return "a meter has never reported";
                if (float.IsNaN(ForwardWatts) || float.IsNaN(ReflectedWatts))
                    return "a meter read NaN";
                if (SkewMilliseconds > MaxSkewMilliseconds)
                    return "forward and reflected were " + SkewMilliseconds.ToString("F0")
                           + " ms apart, so they are not one sample";
                if (AgeMilliseconds > MaxAgeMilliseconds)
                    return "the meters last reported " + AgeMilliseconds.ToString("F0")
                           + " ms ago";
                return "";
            }
        }

        public override string ToString() =>
            !_hasBothMeters
                ? "no reading"
                : ForwardWatts.ToString("F1") + " W fwd, " + ReflectedWatts.ToString("F2")
                  + " W refl, skew " + SkewMilliseconds.ToString("F0") + " ms, age "
                  + AgeMilliseconds.ToString("F0") + " ms";
    }
}
