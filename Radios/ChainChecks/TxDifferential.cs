using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Two transmit runs down the same chain, differing in exactly one stage:
    /// one with audio INJECTED past the microphone, one with the operator
    /// speaking into it. The answer is the comparison.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noel, 2026-08-25, for Don:</b> "He needs an automated test possibility
    /// as well as being able to talk into the microphone. If it's broken at his
    /// microphone side, then it'll show, if we can inject stuff directly."
    /// </para>
    /// <para>
    /// That is a bisection of the transmit chain, and it needs no expertise to
    /// interpret. Injected good and spoken bad puts the fault at the microphone
    /// side — device, gain, mute, privacy setting, cable, profile. Both bad puts
    /// it downstream of the injection point. Both good means intermittent, or
    /// the far end. An operator can run it and report an OUTCOME rather than a
    /// theory, which is the whole point of giving it to a tester.
    /// </para>
    /// <para>
    /// <b>The comparison is only evidence if both runs measured the same
    /// things.</b> That is the discipline this class exists to enforce, and it
    /// is the same one that governs every measurement in this project: a
    /// difference between two runs proves something about the CHAIN only when
    /// it cannot be a difference in the INSTRUMENT. So the watched set is fixed,
    /// both runs are asked for all of it, and a meter that reported in one run
    /// and not the other yields <see cref="Verdict.Incomparable"/> — never a
    /// difference.
    /// </para>
    /// <para>
    /// <b>A skipped run is not a passed run.</b> Step one succeeding means
    /// transmit works FROM THE INJECTION POINT ONWARD; it says nothing about the
    /// microphone, which was never in the path. The natural misreading is "step
    /// one passed, so transmit works", and <see cref="TxRunSample.Ran"/> plus
    /// its reason exist so no report can be written that way.
    /// </para>
    /// </remarks>
    public static class TxDifferential
    {
        /// <summary>
        /// The meters both runs capture, by the radio's own names.
        /// </summary>
        /// <remarks>
        /// SC_MIC is the one that matters — it sits DOWNSTREAM of the mic
        /// selection, so it reads the transmit chain whichever source feeds it,
        /// which is exactly what makes an injected-versus-spoken comparison
        /// legitimate. The rest bracket it: ALC says how hard the radio is being
        /// driven, forward power and SWR say what left the radio and what came
        /// back.
        /// <para>
        /// MIC and MICPEAK are here DELIBERATELY even though they read the
        /// physical mic jack and sit at −120 under PC audio by design. That −120
        /// cost a whole day on 2026-08-23 when it was read as "transmit is
        /// broken", and a differential is precisely where it becomes useful
        /// instead of misleading: it should be −120 in the injected run and NOT
        /// in a run where somebody spoke into the radio's own jack. Recording it
        /// turns a trap into a discriminator.
        /// </para>
        /// </remarks>
        public static readonly string[] Watched =
        {
            "SC_MIC", "ALC", "MIC", "MICPEAK", "FWDPWR", "SWR",
        };

        /// <summary>Which of the two runs a sample came from.</summary>
        public enum RunKind
        {
            /// <summary>Audio injected past the microphone — tone and generated voice.</summary>
            Injected,
            /// <summary>The operator speaking into their microphone.</summary>
            Spoken,
        }

        /// <summary>Why a run did not happen. Never collapse these into "skipped".</summary>
        /// <remarks>
        /// Noel gave two distinct buttons on purpose, and they narrow the fault
        /// domain differently. "Cannot speak into the radio" means the rig is
        /// remote — Don's 6300 lives at Tony's — so the radio's own mic jack is
        /// out of reach, but a PC microphone over the link may still exist and
        /// the comparison may still be possible. "No microphone at all" closes
        /// the comparison entirely. A report that treated them the same would
        /// claim less than it knows in one case and more in the other.
        /// </remarks>
        public enum SkipReason
        {
            /// <summary>The run happened.</summary>
            None = 0,
            /// <summary>"I can't speak directly into my radio" — the rig is not in the room.</summary>
            RadioNotReachable,
            /// <summary>"I don't have access to a microphone" — no mic on either side.</summary>
            NoMicrophone,
        }

        /// <summary>One meter as it read during one run.</summary>
        public readonly struct MeterSample
        {
            public readonly string Name;
            public readonly double Value;
            public readonly string Units;
            /// <summary>False when the radio never reported this meter during the run.</summary>
            public readonly bool Reported;

            public MeterSample(string name, double value, string units, bool reported)
            {
                Name = name ?? "";
                Value = value;
                Units = units ?? "";
                Reported = reported;
            }

            public string Describe() => Reported
                ? Name + " " + Value.ToString("0.##", CultureInfo.InvariantCulture)
                       + (Units.Length > 0 ? " " + Units : "")
                : Name + " not reported";
        }

        /// <summary>What one run measured, or why it did not happen.</summary>
        public sealed class TxRunSample
        {
            public RunKind Kind { get; }
            public bool Ran { get; }
            public SkipReason Skipped { get; }
            public DateTime AtUtc { get; }
            public IReadOnlyList<MeterSample> Meters { get; }

            /// <summary>Conditions, so a reader can reproduce them rather than take our word.</summary>
            public string Frequency { get; }
            public string Mode { get; }
            public string Antenna { get; }

            private TxRunSample(RunKind kind, bool ran, SkipReason skipped, DateTime atUtc,
                                IReadOnlyList<MeterSample> meters,
                                string frequency, string mode, string antenna)
            {
                Kind = kind;
                Ran = ran;
                Skipped = skipped;
                AtUtc = atUtc;
                Meters = meters ?? Array.Empty<MeterSample>();
                Frequency = frequency ?? "";
                Mode = mode ?? "";
                Antenna = antenna ?? "";
            }

            public static TxRunSample Measured(RunKind kind, DateTime atUtc,
                                               IReadOnlyList<MeterSample> meters,
                                               string frequency, string mode, string antenna)
                => new TxRunSample(kind, true, SkipReason.None, atUtc, meters, frequency, mode, antenna);

            public static TxRunSample NotRun(RunKind kind, SkipReason why)
                => new TxRunSample(kind, false, why, DateTime.UtcNow, null, "", "", "");

            public MeterSample? Find(string name) =>
                Meters.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                      .Select(m => (MeterSample?)m)
                      .FirstOrDefault();
        }

        /// <summary>What the comparison of one meter across the two runs says.</summary>
        public enum Verdict
        {
            /// <summary>One run did not report it, so the two cannot be compared.</summary>
            Incomparable,
            /// <summary>Close enough to be the same reading.</summary>
            Same,
            /// <summary>The spoken run read lower than the injected one.</summary>
            LowerWhenSpoken,
            /// <summary>The spoken run read higher than the injected one.</summary>
            HigherWhenSpoken,
        }

        /// <summary>
        /// How far apart two readings must be before the difference is called a
        /// difference rather than noise.
        /// </summary>
        /// <remarks>
        /// Three decibels: a doubling or halving of power, comfortably outside
        /// meter jitter, and far below the tens of dB that separate "audio is
        /// arriving" from "audio is not". Deliberately blunt — this comparison
        /// is meant to catch a dead stage, not to characterise a chain.
        /// </remarks>
        public const double SignificantDelta = 3.0;

        /// <summary>
        /// The SC_MIC level above which transmit audio counts as having
        /// REACHED the radio.
        /// </summary>
        /// <remarks>
        /// One line, shared by everything that answers "did audio arrive":
        /// this comparison, and the Fixer's injected and spoken probes
        /// (<see cref="TxAudioProbe"/>). Two thresholds would let the same
        /// SC_MIC reading arrive in one stage and fail in the next, and an
        /// operator reading both would rightly conclude the tool cannot make
        /// up its mind. The figure sits far above the −120 the meter pins at
        /// when nothing feeds the chain, and far below anything a real signal
        /// reads — it separates "audio" from "no audio", not "good" from
        /// "bad", which is <c>MicAudioReport</c>'s job.
        /// </remarks>
        public const double ReachedRadioDbfs = -45.0;

        /// <summary>One meter, compared across the two runs.</summary>
        public readonly struct MeterComparison
        {
            public readonly string Name;
            public readonly Verdict Verdict;
            public readonly MeterSample? Injected;
            public readonly MeterSample? Spoken;

            public MeterComparison(string name, Verdict verdict, MeterSample? injected, MeterSample? spoken)
            {
                Name = name ?? "";
                Verdict = verdict;
                Injected = injected;
                Spoken = spoken;
            }

            /// <summary>
            /// One line, phrased as an OBSERVATION rather than a diagnosis.
            /// </summary>
            /// <remarks>
            /// Noel, 2026-08-25: "We need to tell them what we find, but not
            /// force them to the conclusion. Give them info to come to the
            /// conclusion." A line that says a stage is BROKEN invites an
            /// argument about whether we are qualified to say so; a line that
            /// says what a meter READ can only be checked or refuted. See #217.
            /// </remarks>
            public string Line()
            {
                string inj = Injected?.Describe() ?? (Name + " not measured");
                string spk = Spoken?.Describe() ?? (Name + " not measured");
                return Verdict switch
                {
                    Verdict.Incomparable =>
                        Name + ": not comparable — injected run " +
                        (Injected?.Reported == true ? "reported, " : "did not report, ") +
                        "spoken run " + (Spoken?.Reported == true ? "reported." : "did not report."),
                    Verdict.Same =>
                        Name + ": injected " + inj + ", spoken " + spk + " — within "
                        + SignificantDelta.ToString("0.#", CultureInfo.InvariantCulture) + " of each other.",
                    _ =>
                        Name + ": injected " + inj + ", spoken " + spk + " — the spoken run read "
                        + Math.Abs((Spoken?.Value ?? 0) - (Injected?.Value ?? 0))
                              .ToString("0.#", CultureInfo.InvariantCulture)
                        + " " + (Verdict == Verdict.LowerWhenSpoken ? "lower" : "higher") + ".",
                };
            }
        }

        /// <summary>
        /// Compare one meter across the runs.
        /// </summary>
        public static MeterComparison CompareMeter(string name, TxRunSample injected, TxRunSample spoken)
        {
            MeterSample? a = injected != null && injected.Ran ? injected.Find(name) : null;
            MeterSample? b = spoken != null && spoken.Ran ? spoken.Find(name) : null;

            // Missing, unreported, or from a run that did not happen: not a
            // difference. A difference between two runs proves something about
            // the chain only when it cannot be a difference in the instrument.
            if (a?.Reported != true || b?.Reported != true)
                return new MeterComparison(name, Verdict.Incomparable, a, b);

            double delta = b.Value.Value - a.Value.Value;
            Verdict v = Math.Abs(delta) < SignificantDelta ? Verdict.Same
                      : delta < 0 ? Verdict.LowerWhenSpoken
                      : Verdict.HigherWhenSpoken;
            return new MeterComparison(name, v, a, b);
        }

        /// <summary>Compare every watched meter across the two runs.</summary>
        public static IReadOnlyList<MeterComparison> Compare(TxRunSample injected, TxRunSample spoken)
            => Watched.Select(n => CompareMeter(n, injected, spoken)).ToList();

        /// <summary>
        /// What the pair of runs supports, in the app's own voice.
        /// </summary>
        /// <remarks>
        /// <b>This is for OUR OPERATOR, not for the vendor.</b> To Don we should
        /// say plainly what it looks like and what to check — supporting our own
        /// user is our job, and being coy with them helps nobody. To FlexRadio
        /// the same measurements go as observations with no conclusion attached
        /// (#217). Same data, two audiences, two grammars. Do not let the
        /// vendor-facing rule make the app reticent with the person using it.
        /// </remarks>
        public static string OperatorSummary(TxRunSample injected, TxRunSample spoken)
        {
            if (injected == null || !injected.Ran)
                return "The injected run did not happen, so there is nothing to compare yet. "
                     + "Run the injected step first — it needs no microphone.";

            MeterSample? injMic = injected.Find("SC_MIC");
            bool injectedReachedRadio = injMic?.Reported == true && injMic.Value.Value > ReachedRadioDbfs;

            if (spoken == null || !spoken.Ran)
            {
                string why = spoken?.Skipped switch
                {
                    SkipReason.RadioNotReachable =>
                        "You said you cannot speak into this radio directly, so the microphone half "
                        + "was not tested. If there is a microphone on the computer you are using, "
                        + "running that half would still narrow this down.",
                    SkipReason.NoMicrophone =>
                        "You said no microphone is available, so the microphone half could not be "
                        + "tested at all.",
                    _ => "The spoken run has not been done yet.",
                };
                return (injectedReachedRadio
                        ? "Injected audio reached the radio. That proves the chain works from the "
                        + "injection point onward — it does NOT tell us anything about your "
                        + "microphone, which was never in the path. "
                        : "Injected audio did not reach the radio, so something is wrong downstream "
                        + "of the microphone. Fixing that comes first. ")
                     + why;
            }

            MeterSample? spkMic = spoken.Find("SC_MIC");
            bool spokenReachedRadio = spkMic?.Reported == true && spkMic.Value.Value > ReachedRadioDbfs;

            if (injectedReachedRadio && !spokenReachedRadio)
                return "Injected audio reached the radio and your voice did not. Everything after the "
                     + "microphone is working, so the problem is on the microphone side — the device "
                     + "selected, its level, whether Windows has it muted or blocked by a privacy "
                     + "setting, the cable, or the microphone profile on the radio.";

            if (!injectedReachedRadio && !spokenReachedRadio)
                return "Neither injected audio nor your voice reached the radio, so the problem is "
                     + "downstream of the microphone — the same for both. Your microphone is not "
                     + "implicated by this test.";

            if (!injectedReachedRadio)
                return "Your voice reached the radio but the injected audio did not. That is unusual "
                     + "and worth reporting as-is rather than acting on.";

            return "Both runs reached the radio. Transmit audio is working on this path, so anything "
                 + "being reported is either intermittent or is happening at the far end.";
        }
    }
}
