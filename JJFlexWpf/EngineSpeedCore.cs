using System;
using System.Diagnostics;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// What one timed run of an engine, or a spectral and neural pair, cost on
    /// this computer. Times are per 10 ms packet, every stage and every channel
    /// together.
    /// </summary>
    /// <param name="Frames">Packets timed.</param>
    /// <param name="ProcessingFactor">Time spent processing over the audio's own length: under 1 keeps up.</param>
    /// <param name="WallFactor">The whole run's wall time over the audio's length, the measuring loop included.</param>
    public sealed record EngineSpeedFigures(int Frames, double MeanMs, double P99Ms, double WorstMs, int Over5, int Over10,
        double ProcessingFactor, double WallFactor, double CpuSeconds, double WallSeconds)
    {
        /// <summary>The length of one packet of audio, in milliseconds.</summary>
        public const double PacketMs = EngineSpeedCore.FrameLength * 1000.0 / EngineSpeedCore.SampleRate;
    }

    /// <summary>
    /// The one measuring core for noise reduction speed (Sprint 49 Track L;
    /// #866: "the bench tool's CPU kit is the measuring core to reuse, not a
    /// second one to write"). Moved here, unchanged in method, from
    /// <c>tools/nrbench</c>'s <c>cpu</c> command, which now links this file and
    /// calls it; the Audio Workshop's speed test calls it too. So the console
    /// kit and the Workshop measure the same way, from the same generated
    /// noise, and a change to the method changes both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method, as the kit has always had it: ten seconds of generated noise
    /// (seed 49) by default, fed one 10 ms packet at a time, the same mono
    /// signal in every channel, each call timed with
    /// <see cref="Stopwatch.GetTimestamp"/>. Building the engines and capturing
    /// a noise profile are outside the timed interval. No device is opened.
    /// </para>
    /// <para>
    /// It runs as fast as it can, not at the radio's pace: what it measures is
    /// the processing a packet costs, which does not depend on when the packet
    /// arrives. Never call it on the receive audio thread.
    /// </para>
    /// </remarks>
    public static class EngineSpeedCore
    {
        public const int SampleRate = 48000, FrameLength = 480;

        /// <summary>The kit's seed, so every run of the kit and the Workshop measures the same signal.</summary>
        public const int DefaultSeed = 49;

        /// <summary>The kit's length: ten seconds, a thousand packets.</summary>
        public const int DefaultSeconds = 10;

        /// <summary>Gaussian noise at the kit's level (standard deviation 0.025), mono, at <see cref="SampleRate"/>.</summary>
        public static float[] GeneratedNoise(int seconds, int seed = DefaultSeed)
        {
            var r = new Random(seed);
            var noise = new float[seconds * SampleRate];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)(Gaussian(r) * .025);
            return noise;
        }

        /// <summary>A standard normal deviate (Box-Muller), as the kit has always drawn it.</summary>
        public static double Gaussian(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>
        /// Time <paramref name="process"/> over <paramref name="mono"/>, one
        /// packet at a time, the same samples in every channel. The buffer handed
        /// over is the same array every call, whole packets only (a short last
        /// packet is padded with silence), and is processed in place.
        /// </summary>
        /// <param name="cancel">Checked between packets; a cancelled run throws <see cref="OperationCanceledException"/>.</param>
        public static EngineSpeedFigures Time(float[] mono, int channels, Action<float[]> process, CancellationToken cancel = default)
        {
            if (mono == null) throw new ArgumentNullException(nameof(mono));
            if (process == null) throw new ArgumentNullException(nameof(process));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            int packets = (mono.Length + FrameLength - 1) / FrameLength;
            if (packets == 0) throw new ArgumentException("Nothing to time.", nameof(mono));

            var buffer = new float[FrameLength * channels];
            var times = new double[packets];
            using var self = Process.GetCurrentProcess();
            var cpuBefore = self.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            int n = 0;
            for (int pos = 0; pos < mono.Length; pos += FrameLength)
            {
                cancel.ThrowIfCancellationRequested();
                Array.Clear(buffer);
                int count = Math.Min(FrameLength, mono.Length - pos);
                for (int i = 0; i < count; i++) for (int c = 0; c < channels; c++) buffer[i * channels + c] = mono[pos + i];
                long t = Stopwatch.GetTimestamp();
                process(buffer);
                times[n++] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            }
            wall.Stop();
            self.Refresh();
            var cpu = self.TotalProcessorTime - cpuBefore;

            var sorted = (double[])times.Clone();
            Array.Sort(sorted);
            double sum = 0; int over5 = 0, over10 = 0;
            foreach (double x in times) { sum += x; if (x > 5) over5++; if (x > 10) over10++; }
            double period = EngineSpeedFigures.PacketMs;
            return new EngineSpeedFigures(packets, sum / packets,
                sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .99) - 1)], sorted[^1], over5, over10,
                sum / (packets * period), wall.Elapsed.TotalSeconds / (packets * period / 1000.0),
                cpu.TotalSeconds, wall.Elapsed.TotalSeconds);
        }
    }
}
