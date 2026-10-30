namespace Radios
{
    /// <summary>
    /// Check your audio's side of the one countdown (#835): which plan a method
    /// gets, and the words it opens with. Pure apart from the lexicon, so a test
    /// can hold every case without a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every method Start runs counts down, the zero-power microphone test
    /// included.</b> The brief left open whether a method that puts nothing on
    /// the air should; this is Claude's answer, for Noel to rule. The
    /// microphone test still KEYS the radio (at zero power), and the countdown
    /// is as much the operator's moment to get the microphone to their face
    /// (Noel 21:34, the reason the length is a setting) as it is a warning of
    /// RF. The same holds for the computer-microphone check Track A is adding;
    /// that method is not in this tree, so it is a recommendation for the
    /// merge, not code here.
    /// </para>
    /// </remarks>
    public static class CheckCountdown
    {
        /// <summary>The lexicon key of a method's short name, as the lead-in says it.</summary>
        // DRAFT PROSE (#835): awaiting Noel. The short names, in Radios/Lexicon/audio.json.
        public static string ShortNameKey(AudioCheckListenMethods method) => method switch
        {
            AudioCheckListenMethods.MicTest => "audio.countdown.method.mic_test",
            AudioCheckListenMethods.Monitor => "audio.countdown.method.monitor",
            AudioCheckListenMethods.RecordPlayback => "audio.countdown.method.record",
            AudioCheckListenMethods.OffAir => "audio.countdown.method.off_air",
            AudioCheckListenMethods.Loopback => "audio.countdown.method.loopback",
            AudioCheckListenMethods.ComputerMic => "audio.countdown.method.computer_mic",
            _ => "audio.countdown.method.other",
        };

        /// <summary>"Starting (the method) in", spoken one beat before the first number.</summary>
        // DRAFT PROSE (#835): awaiting Noel.
        public static string LeadIn(AudioCheckListenMethods method) =>
            Lexicon.Get("audio.countdown.lead_in", ("method", Lexicon.Get(ShortNameKey(method))));

        /// <summary>
        /// The countdown a check of <paramref name="method"/> plays before it
        /// keys, or null when the operator has turned the countdown off.
        /// </summary>
        /// <param name="method">The method Start is running.</param>
        /// <param name="settings">The operator's countdown settings.</param>
        /// <param name="soundsOn">Whether a transmit sound would be heard: the master switch and the transmit family's.</param>
        /// <param name="verbosity">The current speech verbosity.</param>
        public static CountdownPlan? PlanFor(AudioCheckListenMethods method, CountdownSettings settings,
            bool soundsOn, VerbosityLevel verbosity)
        {
            var s = (settings ?? new CountdownSettings()).Clamped();
            if (!s.Enabled) return null;
            return CountdownPlan.For(s, CountdownPlan.VoicingFor(soundsOn, verbosity),
                s.LeadIn ? LeadIn(method) : null);
        }

        /// <summary>
        /// The plan Settings' Play sample button plays: the operator's settings
        /// exactly as a check would use them, with the lead-in naming the
        /// microphone test, the first method. Played even when the countdown is
        /// turned off, so a choice can be heard before it is switched on.
        /// </summary>
        public static CountdownPlan SamplePlan(CountdownSettings settings, bool soundsOn, VerbosityLevel verbosity)
        {
            var s = (settings ?? new CountdownSettings()).Clamped();
            return CountdownPlan.For(s, CountdownPlan.VoicingFor(soundsOn, verbosity),
                s.LeadIn ? LeadIn(AudioCheckListenMethods.MicTest) : null);
        }
    }
}
