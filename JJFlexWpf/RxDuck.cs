using System;
using System.Threading;

namespace JJFlexWpf
{
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

        /// <summary>Time to reach full duck. Short enough to be under the
        /// earcon's own attack, long enough not to click.</summary>
        public const int AttackMs = 25;

        /// <summary>
        /// Time to come back up. Slower than the attack, because a duck that
        /// releases as fast as it engages sounds like a fault rather than like
        /// something making room.
        /// </summary>
        public const int ReleaseMs = 90;

        /// <summary>
        /// How long the duck is held past the end of the earcon that asked for
        /// it, so the release begins after the tail rather than during it.
        /// </summary>
        public const int HoldMs = 60;

        private static volatile bool _enabled = true;
        private static float _depthDb = DefaultDepthDb;
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
