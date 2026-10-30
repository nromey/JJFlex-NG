using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JJFlexWpf
{
    public static partial class EarconPlayer
    {
        // ── Offline render (#855) ──────────────────────────────────────────
        //
        // Plays a sound into a private mixer on the calling thread and reads
        // the mixer dry, so the samples the alert channel WOULD have played
        // come back as an array and no audio device is ever touched. Three
        // users: tools/soundtiming builds its matched detector for each sound
        // from the sound's own recipe this way (not from a description of it);
        // the synthesis-timing measurement times it; and the tests that pin a
        // rendered sound as unchanged compare through it.
        //
        // WHY A MIXER SUBSTITUTION AND NOT A RENDER API PER SOUND. Every earcon
        // already ends in AlertMixer.AddMixerInput, through one of a handful of
        // paths (voiced, decaying, chirp, wav, swept pan, several inputs at
        // once). Substituting the mixer exercises every one of those paths as
        // production runs them, and a new earcon is covered the day it is
        // written. A parallel render API would be a second description of each
        // sound, which is the drift this project keeps paying for.
        //
        // THREAD-STATIC, so an offline render on one thread cannot divert a
        // sound the app plays on another. The one earcon helper that hops
        // threads (PlayLaterVoiced, a delayed second figure in two auditions)
        // places its figure in the private mixer at its delay instead.
        //
        // NOTHING HERE CAN BEEP. The fallback beep is reached only when the
        // alert mixer is null, and during a render it is the private mixer.

        [ThreadStatic] private static MixingSampleProvider? _captureMixer;

        /// <summary>True while this thread is rendering offline.</summary>
        internal static bool RenderingOffline => _captureMixer != null;

        /// <summary>
        /// Render a sound offline: run <paramref name="play"/> (an earcon
        /// method, called as the app calls it) against a private mixer, then
        /// read that mixer until every input has ended or
        /// <paramref name="maxMs"/> has been rendered. Opens no audio device.
        /// </summary>
        /// <returns>Stereo, interleaved, 32-bit float at <see cref="MixerSampleRate"/>.
        /// Empty when the sound did not play (a switch is off).</returns>
        /// <remarks>
        /// The operator's switches and level trims apply, exactly as they would
        /// to the live sound; a caller that wants the sound regardless checks
        /// <see cref="EarconsEnabled"/> and the category itself. Continuous
        /// earcons never end and are cut at <paramref name="maxMs"/>; do not
        /// render them, because their start methods also hold the provider in
        /// a static field the stop method expects to find in the live mixer.
        /// </remarks>
        public static float[] RenderOffline(Action play, int maxMs = 20000)
        {
            return RenderOfflineTimed(play, maxMs, out _, out _);
        }

        /// <summary>
        /// <see cref="RenderOffline"/>, also returning how long the play call
        /// took (where an earcon renders its samples up front) and how long
        /// reading the mixer dry took (where a provider synthesises as it is
        /// read), in Stopwatch ticks.
        /// </summary>
        public static float[] RenderOfflineTimed(Action play, int maxMs,
            out long playTicks, out long readTicks)
        {
            if (play == null) throw new ArgumentNullException(nameof(play));
            LoadSounds();

            var mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, MixerChannels))
            {
                ReadFully = false,
            };

            var saved = _captureMixer;
            _captureMixer = mixer;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                play();
            }
            finally
            {
                _captureMixer = saved;
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

            // Read in the alert device's own block size, so a provider that
            // behaves differently by block (none should) is read as it is live.
            int block = SampleRate / 10 * MixerChannels;
            long limit = (long)SampleRate * MixerChannels * Math.Max(maxMs, 1) / 1000;
            var output = new List<float>(block * 4);
            var buffer = new float[block];
            long total = 0;
            while (total < limit)
            {
                int want = (int)Math.Min(block, limit - total);
                int n = mixer.Read(buffer.AsSpan(0, want));
                if (n <= 0) break;
                for (int i = 0; i < n; i++) output.Add(buffer[i]);
                total += n;
            }
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

            playTicks = t1 - t0;
            readTicks = t2 - t1;
            return output.ToArray();
        }
    }
}
