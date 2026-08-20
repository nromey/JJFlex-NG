using System;
using System.Collections.Generic;
using System.Globalization;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// What a transmit run intends to do, stated before anything is keyed.
    /// </summary>
    public sealed record TransmitPlan
    {
        /// <summary>Plain-language description of the run, read out to the operator.</summary>
        public required string Purpose { get; init; }

        /// <summary>
        /// The hard power ceiling in watts. Enforced in code, and the harness
        /// approaches it from below rather than setting it and hoping.
        /// </summary>
        public required int PowerCeilingWatts { get; init; }

        /// <summary>Longest single key-down permitted, in seconds.</summary>
        public required double MaxSingleKeyDownSeconds { get; init; }

        /// <summary>Total key-down time permitted for the whole run, in seconds.</summary>
        public required double TotalKeyDownBudgetSeconds { get; init; }

        /// <summary>
        /// Cooling gap as a multiple of the preceding key-down. A value of 4
        /// means a two second transmission must be followed by eight seconds of
        /// receive before the next one is allowed.
        /// </summary>
        public double CoolingRatio { get; init; } = 4.0;

        /// <summary>
        /// How many ATU tune cycles this run may perform. Zero means none.
        /// <para>The tuner is rationed by RELAY WEAR, not by RF. It will tune
        /// happily with no antenna connected, and the cost of doing so is
        /// mechanical: physical relays with a finite number of operations. So
        /// this is a counter enforced in code rather than a comment asking the
        /// author to be careful.</para>
        /// </summary>
        public int AtuTuneBudget { get; init; }

        /// <summary>Estimated total wall-clock time, for the consent prompt.</summary>
        public TimeSpan EstimatedDuration { get; init; } = TimeSpan.Zero;

        public string Describe()
        {
            var lines = new List<string>
            {
                "This run WILL TRANSMIT.",
                "Purpose: " + Purpose,
                string.Create(CultureInfo.InvariantCulture,
                    $"Power ceiling: {PowerCeilingWatts} watts, enforced in code and approached from below."),
                string.Create(CultureInfo.InvariantCulture,
                    $"Longest single transmission: {MaxSingleKeyDownSeconds:F1} seconds."),
                string.Create(CultureInfo.InvariantCulture,
                    $"Total key-down budget for the whole run: {TotalKeyDownBudgetSeconds:F1} seconds."),
                string.Create(CultureInfo.InvariantCulture,
                    $"Cooling gap after each transmission: {CoolingRatio:F1} times its length."),
                AtuTuneBudget == 0
                    ? "Antenna tuner: not used."
                    : string.Create(CultureInfo.InvariantCulture,
                        $"Antenna tuner: at most {AtuTuneBudget} tune cycles. Each one costs relay operations that do not come back."),
            };

            if (EstimatedDuration > TimeSpan.Zero)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Roughly {EstimatedDuration.TotalSeconds:F0} seconds from start to finish."));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// A granted authorisation to transmit, carrying the budget with it.
    ///
    /// <para><b>Consent is never a side effect of constructing something.</b>
    /// There is no public constructor and no default. The only way to obtain one
    /// is <see cref="Grant"/>, which requires the operator to type an exact
    /// confirmation phrase after being told what will happen. A harness that can
    /// key the radio because somebody instantiated a class by accident is not a
    /// harness, it is a hazard.</para>
    ///
    /// <para>The budget is a ledger, not a suggestion. Every keying command
    /// passes through <see cref="Authorise"/>, which refuses once the budget is
    /// spent, refuses inside a cooling gap, and refuses an ATU cycle once the
    /// relay count is used up.</para>
    /// </summary>
    public sealed class TransmitConsent
    {
        /// <summary>What the operator has to type. Nothing shorter counts.</summary>
        public const string ConfirmationPhrase = "TRANSMIT";

        private readonly object _gate = new();
        private double _keyDownSpent;
        private int _atuCyclesSpent;
        private DateTime _lastUnkeyAt = DateTime.MinValue;
        private double _lastKeyDownSeconds;
        private bool _revoked;

        private TransmitConsent(TransmitPlan plan, string grantedBy)
        {
            Plan = plan;
            GrantedBy = grantedBy;
            GrantedAt = DateTimeOffset.Now;
        }

        public TransmitPlan Plan { get; }

        public string GrantedBy { get; }

        public DateTimeOffset GrantedAt { get; }

        public double KeyDownSecondsRemaining
        {
            get { lock (_gate) { return Math.Max(0, Plan.TotalKeyDownBudgetSeconds - _keyDownSpent); } }
        }

        public int AtuCyclesRemaining
        {
            get { lock (_gate) { return Math.Max(0, Plan.AtuTuneBudget - _atuCyclesSpent); } }
        }

        /// <summary>
        /// Asks the operator, in words, and returns a consent only if they type
        /// the confirmation phrase. <paramref name="ask"/> receives the full
        /// description and returns whatever the operator typed.
        /// </summary>
        public static TransmitConsent? Grant(TransmitPlan plan, Func<string, string?> ask, string grantedBy)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(ask);

            if (plan.PowerCeilingWatts <= 0)
            {
                throw new ArgumentException("A transmit plan needs a positive power ceiling.", nameof(plan));
            }
            if (plan.TotalKeyDownBudgetSeconds <= 0)
            {
                throw new ArgumentException("A transmit plan needs a positive key-down budget.", nameof(plan));
            }

            string prompt = plan.Describe()
                + Environment.NewLine
                + Environment.NewLine
                + $"Type {ConfirmationPhrase} to authorise this run, or anything else to cancel.";

            string? answer = ask(prompt);
            return string.Equals(answer?.Trim(), ConfirmationPhrase, StringComparison.Ordinal)
                ? new TransmitConsent(plan, grantedBy)
                : null;
        }

        /// <summary>
        /// Ends the authorisation. Called when a run finishes or aborts, so a
        /// consent object that outlives its run cannot be reused.
        /// </summary>
        public void Revoke()
        {
            lock (_gate) { _revoked = true; }
        }

        /// <summary>
        /// Checks one command against the ledger. Throws rather than returning
        /// false: a caller that ignores a boolean would key the radio anyway.
        /// </summary>
        public void Authorise(string command, CommandEffect effect)
        {
            lock (_gate)
            {
                if (_revoked)
                {
                    throw new TransmitRefusedException(
                        $"Refused '{command}': this authorisation has already been closed out.");
                }

                if (effect == CommandEffect.Silent) return;

                if (KeyDownSecondsRemainingLocked() <= 0)
                {
                    throw new TransmitRefusedException(
                        $"Refused '{command}': the run's key-down budget of " +
                        $"{Plan.TotalKeyDownBudgetSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds is spent.");
                }

                double gapRequired = _lastKeyDownSeconds * Plan.CoolingRatio;
                if (gapRequired > 0)
                {
                    double gapSoFar = (DateTime.UtcNow - _lastUnkeyAt).TotalSeconds;
                    if (gapSoFar < gapRequired)
                    {
                        throw new TransmitRefusedException(
                            $"Refused '{command}': still cooling. " +
                            $"{gapRequired.ToString("F1", CultureInfo.InvariantCulture)} seconds required after the last " +
                            $"{_lastKeyDownSeconds.ToString("F1", CultureInfo.InvariantCulture)} second transmission, " +
                            $"{gapSoFar.ToString("F1", CultureInfo.InvariantCulture)} elapsed.");
                    }
                }

                if (effect == CommandEffect.KeysAndWearsRelays)
                {
                    if (AtuCyclesRemainingLocked() <= 0)
                    {
                        throw new TransmitRefusedException(
                            $"Refused '{command}': the ATU relay budget for this run " +
                            $"({Plan.AtuTuneBudget}) is used up. Relay operations do not come back.");
                    }
                    _atuCyclesSpent++;
                }
            }
        }

        /// <summary>
        /// Records a completed key-down. The harness calls this after unkeying,
        /// which is what makes the cooling gap and the total budget real rather
        /// than nominal.
        /// </summary>
        public void RecordKeyDown(TimeSpan duration)
        {
            lock (_gate)
            {
                _keyDownSpent += duration.TotalSeconds;
                _lastKeyDownSeconds = duration.TotalSeconds;
                _lastUnkeyAt = DateTime.UtcNow;
            }
        }

        /// <summary>Clamps a requested power to the ceiling the operator set.</summary>
        public int ClampPower(int requestedWatts)
        {
            if (requestedWatts < 0) return 0;
            return Math.Min(requestedWatts, Plan.PowerCeilingWatts);
        }

        /// <summary>Clamps a requested key-down to the per-transmission ceiling.</summary>
        public TimeSpan ClampKeyDown(TimeSpan requested)
        {
            double seconds = Math.Min(requested.TotalSeconds, Plan.MaxSingleKeyDownSeconds);
            lock (_gate)
            {
                seconds = Math.Min(seconds, KeyDownSecondsRemainingLocked());
            }
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        public string Summarise()
        {
            lock (_gate)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Transmit budget: {_keyDownSpent:F1} of {Plan.TotalKeyDownBudgetSeconds:F1} seconds key-down used, " +
                    $"{_atuCyclesSpent} of {Plan.AtuTuneBudget} ATU cycles used, ceiling {Plan.PowerCeilingWatts} watts.");
            }
        }

        private double KeyDownSecondsRemainingLocked() => Plan.TotalKeyDownBudgetSeconds - _keyDownSpent;

        private int AtuCyclesRemainingLocked() => Plan.AtuTuneBudget - _atuCyclesSpent;
    }
}
