using System;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// How the warning duck moves: how abruptly the band drops, how long it
    /// stays down after the sound, and how abruptly it comes back (#535).
    /// </summary>
    /// <remarks>
    /// Three named steps rather than three millisecond controls — the reasoning
    /// is on <see cref="RxDuck"/>. The numeric values are deliberate, so a
    /// persisted name that fails to parse can be told from a real one.
    /// </remarks>
    public enum RxDuckTimingPreset
    {
        /// <summary>The shipped timing: in fast, out fast. The dip snaps in
        /// and out with the sound.</summary>
        Quick = 0,

        /// <summary>Eases in, stays down a little longer, eases back.</summary>
        Gentle = 1,

        /// <summary>Eases in, stays down noticeably after the warning, and
        /// comes back slowly — the band breathes rather than steps.</summary>
        Lingering = 2,
    }

    /// <summary>
    /// Pulls the radio's PC audio down a few dB while a warning earcon is
    /// sounding, so the alert is heard over the band rather than in it (#116).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WARNINGS ONLY.</b> Ruled by Noel 2026-08-21, choosing this over
    /// ducking every earcon: a duck on every keyclick and toggle would pump
    /// the band audio constantly, which is its own kind of fatigue and
    /// arguably worse than the masking it fixes. The alert family is where
    /// cutting through actually matters.
    /// </para>
    /// <para>
    /// <b>This is a PARTIAL fix by construction, and that is understood.</b>
    /// There are three listening topologies and ducking reaches one. PC audio
    /// through the computer — the app owns the path, so this works. Listening
    /// at the rig's speaker or headphones — the app has no access to that
    /// audio at all; the earcon comes from the computer, the band noise comes
    /// from the radio, and they mix in the room. Both at once, which is a
    /// common remote setup — this helps the PC half and does nothing for the
    /// rig half. Earcon DESIGN is the only thing that reaches all three, which
    /// is why #115 came first and why this is an enhancement rather than the
    /// answer.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT done by turning the radio's own levels down</b>,
    /// even though FlexLib allows it. Headphone and line-out gain are
    /// RADIO-SIDE SHARED STATE: in MultiFlex another operator is on that radio
    /// and ducking their audio for our earcon is unacceptable. Network latency
    /// would land the duck after the earcon finished. It fights the operator's
    /// own level settings, which are intents we must not silently override.
    /// And if the app died mid-earcon the radio would be left at a wrong level
    /// with nothing to restore it.
    /// </para>
    /// <para>
    /// <b>THE SCREEN READER DUCKS TOO, AND THE TWO MULTIPLY — so this one is
    /// scoped to the only sound the reader cannot know about (#436).</b> NVDA
    /// and JAWS both duck natively, at the Windows session mixer, for their
    /// own speech; ours is a gain multiplier inside our own pipeline. When
    /// both engage the band is attenuated twice — a modest four dB under a
    /// reader's fourteen is roughly eighteen, which is exactly the hole in the
    /// band the four dB above was chosen to avoid. Ruled by Noel 2026-08-31:
    /// <i>"I'd just let NVDA handle ducking or JAWS."</i>
    /// </para>
    /// <para>
    /// <b>The warning earcon is the exception, and it is the reason this class
    /// still exists.</b> The reader has no idea an earcon is playing, so it
    /// will never duck for one — and its duck pulls OUR earcon down by the
    /// same amount as the band, since both leave this process on the same
    /// audio session. It therefore makes no room for the alert at all. This
    /// duck is the only thing that gives a warning an edge over the band it
    /// has to be heard through. So: the reader ducks for speech, we duck for
    /// our own warning sounds, and neither ducks for the other's. Anything
    /// the reader can hear about is the reader's to duck for, at the depth the
    /// operator already chose in their reader — never here.
    /// <c>Radios.Tests/WarningDuckScopeTests</c> is what keeps that true, and
    /// it is a test rather than this paragraph because a request added to a
    /// keyclick or a speech path breaks nothing, fails no build, and simply
    /// starts taking the band away at moments the reader is already taking it.
    /// </para>
    /// <para>
    /// <b>THE DUCK CANNOT OUTLIVE THE EARCON, and not because something
    /// remembers to end it.</b> A start/stop pair would leave RX permanently
    /// attenuated if the stop were ever missed — an exception on the earcon
    /// path, a crash mid-tone — with no way back and nothing visibly broken to
    /// point at. So there is no stop. A request writes a DEADLINE, the audio
    /// thread compares it against the clock, and the gain glides home on its
    /// own when the deadline passes. A process that dies mid-duck simply
    /// stops processing audio; a request that is never followed up expires.
    /// The failure mode is designed out rather than watched for.
    /// <c>Radios.Tests/WarningDuckDeadlineTests</c> pins it: the deadline is
    /// written in exactly one method and nothing in this class can end a duck
    /// early.
    /// </para>
    /// <para>
    /// <b>TIMING IS A NAMED LADDER, NOT THREE SLIDERS (#535).</b> Attack, hold
    /// and release were compile-time constants until 2026-09-05, so when the
    /// dip read as a distraction in use (#534) the only thing anyone could turn
    /// was the depth — and a dip that distracts rather than helps is usually a
    /// timing complaint, not a level one. 25 ms in and 90 ms out is gate-fast
    /// by the standard of every broadcast ducker, which releases over hundreds
    /// of milliseconds to a second; at that speed the dip is an event with two
    /// audible edges. Three millisecond controls were rejected because the
    /// three numbers interact — the safe range of each depends on the other
    /// two and on the depth, none of which a slider can show — and because
    /// milliseconds are the mechanism, not the experience: nobody hears "60 ms
    /// of hold", they hear "it lets go too soon". A bare quick/gentle pair was
    /// rejected because two points cannot show a direction: if gentle still
    /// distracts, the operator is stuck. So: three named steps, each a
    /// coherent triple. <see cref="RxDuckTimingPreset.Quick"/> is the shipped
    /// timing byte for byte at the shipped depth, kept as the control arm of
    /// the hypothesis; the other two ease and hang progressively.
    /// </para>
    /// <para>
    /// <b>Release scales with depth; attack does not.</b> What the ear hears in
    /// a release is the RATE of return. A fixed 90 ms was 44 dB per second at
    /// four dB and 133 at twelve — the same setting became a fault-shaped
    /// swell as the depth went up. Each preset therefore states its release as
    /// milliseconds per dB, and the time falls out of the depth, floored at the
    /// attack (a duck that lets go faster than it grabbed sounds like a fault)
    /// and capped at <see cref="MaxReleaseMs"/> so the band is never coming
    /// back for longer than that after an alert. Attack stays a fixed time per
    /// preset because its ceiling is external, not perceptual: the duck must be
    /// at depth before the earcon's first note is fully up, or it is making
    /// room after the guest has arrived.
    /// </para>
    /// <para>
    /// <b>The duck LEADS the earcon, and that bounds the attack from above.</b>
    /// <see cref="RequestFor"/> is called before the earcon is queued; the gain
    /// is applied at decode time and then waits in the receive playback queue,
    /// while the earcon leaves through the alert device's own buffers, which
    /// are deeper. So the band starts dipping some tens of milliseconds before
    /// the alarm is heard — which is why a fast attack is its own audible
    /// event, and also why the attack has more headroom than the earcon's own
    /// onset suggests. The gentlest preset still lands well inside the shortest
    /// warning's first note (90 ms). Estimated from the buffer geometry, not
    /// measured; see task #535.
    /// </para>
    /// </remarks>
    public static class RxDuck
    {
        /// <summary>Deepest duck the operator may configure.</summary>
        public const float MaxDepthDb = 12f;

        /// <summary>
        /// The default depth, in dB of attenuation.
        /// </summary>
        /// <remarks>
        /// Modest on purpose. An operator copying weak CW under a warning will
        /// not thank us for a twelve dB hole in the band, and the point is to
        /// let the alert through rather than to clear the channel for it.
        /// </remarks>
        public const float DefaultDepthDb = 4f;

        /// <summary>
        /// The longest the band is ever on its way back after a duck, whatever
        /// the preset and depth. A bound the operator can rely on, and a test
        /// can assert.
        /// </summary>
        public const int MaxReleaseMs = 1500;

        /// <summary>
        /// The timing in force until a config is loaded, and for any persisted
        /// name that does not parse. Quick, because it is the behaviour that
        /// shipped and the control arm of the #535 hypothesis; once the
        /// listening test has been done, moving this is one line here and one
        /// in <c>AudioOutputConfig</c>, and the two must agree.
        /// </summary>
        public const RxDuckTimingPreset DefaultTiming = RxDuckTimingPreset.Quick;

        /// <summary>
        /// One step of the ladder. Attack and hold are times; release is a rate,
        /// because the ear judges a release by how fast the band comes back,
        /// not by how long it takes.
        /// </summary>
        private readonly struct Preset
        {
            public readonly int AttackMs;
            public readonly int HoldMs;
            public readonly float ReleaseMsPerDb;

            public Preset(int attackMs, int holdMs, float releaseMsPerDb)
            {
                AttackMs = attackMs;
                HoldMs = holdMs;
                ReleaseMsPerDb = releaseMsPerDb;
            }
        }

        // Indexed by RxDuckTimingPreset. Quick is 25 / 60 / 90-at-four-dB, which
        // is exactly what the constants said before #535. Gentle and Lingering
        // each roughly double the step before them on hold and release; their
        // attacks stay under the shortest warning's 90 ms first note with the
        // duck's lead over the earcon in hand.
        private static readonly Preset[] Presets =
        {
            new Preset(attackMs: 25, holdMs: 60,  releaseMsPerDb: 22.5f), // Quick
            new Preset(attackMs: 50, holdMs: 150, releaseMsPerDb: 75f),   // Gentle
            new Preset(attackMs: 75, holdMs: 300, releaseMsPerDb: 150f),  // Lingering
        };

        // OFF by default since 2026-09-03 (#534). AudioOutputConfig is the
        // authority once a config has been loaded; this initialiser is what
        // holds before that, and the two must agree, or the duck would be live
        // for the window between process start and config load.
        private static volatile bool _enabled = false;
        private static float _depthDb = DefaultDepthDb;
        private static volatile int _timing = (int)DefaultTiming;
        private static long _activeUntilTicks;

        /// <summary>
        /// Whether ducking happens at all. Fully defeatable was a requirement,
        /// not a nicety: this attenuates received audio, and an operator who
        /// wants their band untouched is entitled to that.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Depth of the duck in dB of attenuation, 0 to 12.</summary>
        public static float DepthDb
        {
            get => _depthDb;
            set => _depthDb = Math.Clamp(value, 0f, MaxDepthDb);
        }

        /// <summary>
        /// Which step of the timing ladder is in force. Live: the audio thread
        /// reads the attack and release it implies on every buffer, so a change
        /// mid-duck changes how the duck moves from that buffer on. It never
        /// touches the deadline — a request already made keeps its expiry.
        /// </summary>
        public static RxDuckTimingPreset Timing
        {
            get => (RxDuckTimingPreset)_timing;
            set => _timing = (int)(IsDefined(value) ? value : DefaultTiming);
        }

        /// <summary>Time to reach full duck, for the preset in force.</summary>
        public static int AttackMs => AttackMsFor(Timing);

        /// <summary>
        /// How long the duck is held past the end of the earcon that asked for
        /// it, so the release begins after the tail rather than during it. For
        /// the preset in force, read at request time.
        /// </summary>
        public static int HoldMs => HoldMsFor(Timing);

        /// <summary>
        /// Time to come back up, for the preset and depth in force. Never
        /// shorter than the attack, never longer than
        /// <see cref="MaxReleaseMs"/>.
        /// </summary>
        public static int ReleaseMs => ReleaseMsFor(Timing, DepthDb);

        /// <summary>The attack a preset specifies, in milliseconds.</summary>
        public static int AttackMsFor(RxDuckTimingPreset timing) => PresetFor(timing).AttackMs;

        /// <summary>The hold a preset specifies, in milliseconds.</summary>
        public static int HoldMsFor(RxDuckTimingPreset timing) => PresetFor(timing).HoldMs;

        /// <summary>
        /// The release a preset implies at a given depth: its rate times the
        /// depth, floored at the preset's attack and capped at
        /// <see cref="MaxReleaseMs"/>.
        /// </summary>
        public static int ReleaseMsFor(RxDuckTimingPreset timing, float depthDb)
        {
            Preset p = PresetFor(timing);
            float depth = Math.Clamp(depthDb, 0f, MaxDepthDb);
            int raw = (int)Math.Round(p.ReleaseMsPerDb * depth);
            return Math.Clamp(raw, p.AttackMs, MaxReleaseMs);
        }

        /// <summary>
        /// The preset a persisted name means. Lenient on purpose: null, empty,
        /// unknown and wrongly cased names all give <see cref="DefaultTiming"/>
        /// rather than an exception, because the config file this is read from
        /// is hand-editable and survives downgrades, and a value a newer build
        /// wrote must never cost an older one its whole audio configuration.
        /// </summary>
        public static RxDuckTimingPreset ParseTiming(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DefaultTiming;
            // Enum.TryParse accepts a bare integer too, so the IsDefined check is
            // what refuses "7" as well as "Brisk".
            if (Enum.TryParse(name.Trim(), ignoreCase: true, out RxDuckTimingPreset parsed)
                && IsDefined(parsed))
                return parsed;
            return DefaultTiming;
        }

        /// <summary>The name <see cref="ParseTiming"/> round-trips.</summary>
        public static string TimingName(RxDuckTimingPreset timing) =>
            (IsDefined(timing) ? timing : DefaultTiming).ToString();

        private static bool IsDefined(RxDuckTimingPreset timing) =>
            (int)timing >= 0 && (int)timing < Presets.Length;

        private static Preset PresetFor(RxDuckTimingPreset timing) =>
            Presets[IsDefined(timing) ? (int)timing : (int)DefaultTiming];

        /// <summary>
        /// The gain a fully engaged duck lands on. 1.0 when ducking is off or
        /// set to no depth, which makes the whole stage a no-op without
        /// needing a separate branch anywhere.
        /// </summary>
        public static float DuckedGain
        {
            get
            {
                float db = _depthDb;
                if (!_enabled || db < 0.05f) return 1f;
                return (float)Math.Pow(10.0, -db / 20.0);
            }
        }

        /// <summary>
        /// Ask for the duck to be held for the next <paramref name="earconMs"/>
        /// milliseconds, plus the hold that covers the earcon's tail.
        /// </summary>
        /// <remarks>
        /// Extends an existing request rather than replacing it — two warnings
        /// close together must not have the first one's expiry cut the second
        /// one short. Never shortens: a request always takes the later of the
        /// two deadlines.
        /// </remarks>
        public static void RequestFor(int earconMs)
        {
            if (!_enabled || earconMs <= 0) return;

            long until = DateTime.UtcNow.Ticks
                + TimeSpan.TicksPerMillisecond * (earconMs + HoldMs);

            // Later-wins, without a lock: re-read and retry if someone else
            // pushed the deadline out underneath us.
            while (true)
            {
                long current = Interlocked.Read(ref _activeUntilTicks);
                if (current >= until) return;
                if (Interlocked.CompareExchange(ref _activeUntilTicks, until, current) == current)
                    return;
            }
        }

        /// <summary>
        /// The gain the audio path should be heading toward right now: the
        /// ducked gain while a request is live, otherwise 1.
        /// </summary>
        public static float TargetGain =>
            DateTime.UtcNow.Ticks < Interlocked.Read(ref _activeUntilTicks) ? DuckedGain : 1f;
    }
}
