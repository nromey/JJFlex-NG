using System;
using System.Collections.Generic;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Which connection path to try first for a radio, and how a TREND in the
    /// connection history is allowed to influence that (task #79).
    ///
    /// <para><b>The whole contract, and the reason this is a separate class:</b>
    /// a learned value only ever PREFILLS. A stored explicit choice always
    /// wins. That rule has an invisible failure mode — code that violates it
    /// looks identical to code that honours it, right up to the evening an
    /// operator's deliberate setting silently evaporates because the app had
    /// been quietly disagreeing with them for three connects. You cannot see
    /// it in a diff, so it lives in one small pure function that a test can
    /// pin down, instead of inside a WPF property that no test can reach.</para>
    ///
    /// <para>The substrate already existed: <see cref="ConnectionHistory"/> has
    /// recorded a ten-entry per-radio ring of timestamped path, outcome and
    /// duration since it was written, with the policies it was anticipating
    /// named in its own header as deliberately out of scope. Nothing read it
    /// until now.</para>
    /// </summary>
    public static class ConnectPathPolicy
    {
        /// <summary>The outcome string <c>ConnectionHistory</c> records for a
        /// connect that actually succeeded. Failures record a failure class
        /// name instead, so anything that is not this is not a success.</summary>
        public const string ConnectedOutcome = "connected";

        /// <summary>
        /// The outcome for a leg whose session connected and whose radio then
        /// failed to open (task #284).
        ///
        /// <para>Deliberately not <see cref="ConnectedOutcome"/>, which is
        /// what it used to be recorded as, and deliberately its own word
        /// rather than a generic failure: a leg that got all the way to a live
        /// session and still did not produce a working radio is a different
        /// story from one that could not find the radio at all, and the
        /// difference is exactly what someone reading the ring later wants to
        /// know.</para>
        /// </summary>
        public const string OpenFailedOutcome = "open_failed";

        /// <summary>
        /// How many successful connects in a row on one path constitute a
        /// trend worth prefilling.
        ///
        /// <para>Three is the DEFAULT, not the only answer: task #102 made it a
        /// setting (<see cref="ConnectPathLearningConfig"/>, 3 to 5) because the
        /// number is a judgement about how much evidence outweighs inertia, and
        /// an operator who is often travelling wants the app slower to conclude
        /// anything. It is bounded from above by the store: the ring holds ten
        /// ATTEMPTS, and a chain-walking connect writes two of them (the leg
        /// that failed, then the leg that worked), so a radio that habitually
        /// falls back has room for exactly five successes and nothing past five
        /// could ever be reached.</para>
        /// </summary>
        public const int TrendThreshold = 3;

        /// <summary>
        /// The path a radio's history recommends, or null when it recommends
        /// nothing.
        ///
        /// <para>Reads the last <paramref name="threshold"/> SUCCESSFUL
        /// connects and asks whether they all went the same way. Failures are
        /// skipped rather than treated as trend-breaking, and that is a
        /// deliberate choice rather than a convenience: a radio that is only
        /// reachable over SmartLink records a failed local leg before every
        /// single successful remote one, so a rule that reset on any failure
        /// would learn nothing about exactly the radios with the strongest
        /// habit.</para>
        ///
        /// <para>Never throws, never reads a file — hand it the history.</para>
        /// </summary>
        public static ConnectPathKind? LearnFrom(
            IReadOnlyList<ConnectionAttemptRecord>? history, int threshold = TrendThreshold)
        {
            if (history == null || threshold <= 0) return null;

            ConnectPathKind? candidate = null;
            int matched = 0;

            for (int i = history.Count - 1; i >= 0 && matched < threshold; i--)
            {
                var record = history[i];
                if (record == null) continue;
                if (!string.Equals(record.Outcome, ConnectedOutcome, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Enum.TryParse<ConnectPathKind>(record.Path, ignoreCase: true, out var path))
                {
                    // A path this build does not know about — a newer version's
                    // JJ Flexible Connect leg, say. It is a real success on a
                    // real path, so it must not be skipped as though it never
                    // happened; it simply cannot agree with anything we can
                    // name, which ends the run.
                    return null;
                }

                if (candidate == null) candidate = path;
                else if (candidate != path) return null;

                matched++;
            }

            return matched >= threshold ? candidate : null;
        }

        /// <summary>
        /// The same question against the on-disk history for one radio, using
        /// the operator's own setting for how much evidence it takes.
        /// Returns null on any IO trouble — a store we cannot read teaches us
        /// nothing, which is not the same as teaching us "no".
        ///
        /// <para>Returns null unconditionally when the operator has turned
        /// learning off (<see cref="ConnectPathLearningConfig.LearnFromHistory"/>).
        /// The off switch lives HERE, at the one place a trend enters the app,
        /// rather than inside <see cref="Resolve"/> — Resolve is the pure
        /// function that pins the "a choice always wins" contract, and it must
        /// keep answering the same question the tests ask it.</para>
        /// </summary>
        public static ConnectPathKind? LearnForRadioUsingSettings(string serial)
        {
            var cfg = ConnectPathLearningConfig.Current;
            if (!cfg.LearnFromHistory) return null;
            return LearnForRadio(serial, cfg.TrendThreshold);
        }

        /// <summary>
        /// The same question against the on-disk history for one radio at an
        /// explicit threshold. Ignores the on/off setting by design — this is
        /// the mechanism; <see cref="LearnForRadioUsingSettings"/> is the policy
        /// the app should call.
        /// </summary>
        public static ConnectPathKind? LearnForRadio(string serial, int threshold = TrendThreshold)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(serial)) return null;
                var learned = LearnFrom(ConnectionHistory.Load(serial), threshold);
                if (learned != null)
                {
                    Tracing.TraceLine(
                        $"ConnectPathPolicy: {serial} history suggests {learned} "
                        + $"({threshold} successful connects in a row) — prefill only",
                        System.Diagnostics.TraceLevel.Info);
                }
                return learned;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ConnectPathPolicy.LearnForRadio({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// The ordered chain a connect should walk.
        ///
        /// <para>Precedence, and the order is the contract:</para>
        /// <list type="number">
        /// <item>A stored explicit chain. The operator said so; nothing
        /// outranks that, ever, however strong the trend disagreeing with
        /// it.</item>
        /// <item>A learned trend, as a prefill — it only orders a chain that
        /// nobody has ordered, and it may not send a connect out to the
        /// internet for a radio that is answering on this subnet.</item>
        /// <item>The derived default: local first unless the radio's story is
        /// remote.</item>
        /// </list>
        ///
        /// <para><b>Why a trend does not outrank a radio on the LAN (task
        /// #284).</b> A SmartLink habit can only be LEARNED while the radio is
        /// not on your network — that is the condition under which those
        /// connects happened. Replaying it at the moment the radio is
        /// broadcasting from 192.168.50.100 applies evidence outside the
        /// conditions it was gathered under, and the result is what Noel hit
        /// twice on 2026-08-26: his own 8600, one subnet hop away, reached
        /// through FlexRadio's servers, with "I'm not trying smart link, it's
        /// detected local network." Present LAN presence is evidence about
        /// NOW; a trend is evidence about a set of past conditions.</para>
        ///
        /// <para>An operator who genuinely wants SmartLink from inside their
        /// own shack still gets it — by storing that chain (rung 1, which no
        /// trend has ever been allowed to touch) or by forcing the path from
        /// the context menu, which does not come through here at all. What is
        /// removed is the app deciding it on their behalf from a habit.</para>
        ///
        /// <para>Every result carries BOTH paths, which is what makes
        /// automatic fallback ordinary list-walking rather than special-case
        /// logic. Only an operator-stored one-entry chain means "this path
        /// only" — and note that a learned value can never produce one, by
        /// construction. A trend is evidence about what usually works, never
        /// permission to stop trying the other way.</para>
        /// </summary>
        public static List<ConnectPathKind> Resolve(
            IReadOnlyList<ConnectPathKind>? storedChain,
            ConnectPathKind? learned,
            bool lanAvailable,
            bool wanAvailable,
            bool lastSeenRemote)
        {
            // 1. A choice. Returned as stored, length and all.
            if (storedChain != null && storedChain.Count > 0)
                return new List<ConnectPathKind>(storedChain);

            // 2. A trend, prefilling an unordered chain — except that it may
            //    not route around a radio that is answering on this subnet.
            if (learned == ConnectPathKind.SmartLink) return lanAvailable ? LocalFirst() : RemoteFirst();
            if (learned == ConnectPathKind.Local) return LocalFirst();

            // 3. The derived default — the historical behaviour, now explicit.
            bool remoteStory = wanAvailable ? !lanAvailable : (!lanAvailable && lastSeenRemote);
            return remoteStory ? RemoteFirst() : LocalFirst();
        }

        private static List<ConnectPathKind> LocalFirst() =>
            new() { ConnectPathKind.Local, ConnectPathKind.SmartLink };

        private static List<ConnectPathKind> RemoteFirst() =>
            new() { ConnectPathKind.SmartLink, ConnectPathKind.Local };
    }
}
