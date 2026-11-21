using System;
using System.Diagnostics;
using JJPortaudio;

namespace JJFlexWpf
{
    /// <summary>
    /// What each 10 ms block of receive audio is, for the lag shifter (#873 phase
    /// 2, #473): speech, noise, unsure, or neither, plus the slice mode's CW and
    /// digital flags. Run on the receive thread where the audio is decoded, so its
    /// decision leads playback by the queue depth: no decision latency and no
    /// audio latency (the follow-up research, 2026-11-08, section 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The detector is the stored RNNoise v0.1.1 voice branch</b>
    /// (<see cref="RnnoiseVoiceDetector"/>), with a loudness check beside it,
    /// as ruled by Noel 2026-11-20 11:25 (S10): radio audio suits RNNoise's
    /// detector because it was trained on noise; it is analysis only (it reads a
    /// copy and returns a number; the audio never waits on it); v0.1.1 has no
    /// look-ahead, so its decision is at most one frame late, which the hangover
    /// dwarfs; and it is independent of whichever engine is reducing noise, so
    /// the speed-up behaves the same across a model change. The comparison with
    /// other detectors on off-air audio is later work.
    /// </para>
    /// <para>
    /// <b>One detector on the mix of both ears</b>, not one per ear (my
    /// judgement, measured in <c>RxContentClassifierTests</c>): the shifter makes
    /// one splice point for both ears, so it needs one class per block, and the
    /// mix carries speech whichever ear it is in. Voice in one ear and noise in
    /// the other reads as voice, the conservative direction.
    /// </para>
    /// <para>
    /// <b>Thresholds</b> are the follow-up's (section 6): speech at a probability of
    /// 0.3 or more, held for a hangover, OR-ed with the loudness check; unsure
    /// between 0.1 and 0.3; the fast noise policy only after 100 ms of
    /// consecutive blocks that were neither. Biased toward voice on purpose: the
    /// error that matters is speech called noise.
    /// </para>
    /// <para>
    /// If the detector cannot load, every block is <see cref="RxContentClass.Unclassified"/>
    /// (the shifter then treats it as voice and never takes the fast noise
    /// policy), and the CW and digital flags still apply. Allocation-free after
    /// construction; one thread uses it (the remote-audio thread).
    /// </para>
    /// </remarks>
    public sealed class RxContentClassifier : IDisposable
    {
        /// <summary>Speech at or above this voice probability (follow-up, section 6).</summary>
        public const float SpeechThreshold = 0.3f;
        /// <summary>Unsure from this probability up to <see cref="SpeechThreshold"/> (follow-up, section 6).</summary>
        public const float UncertainThreshold = 0.1f;
        /// <summary>Speech is held this long after the probability falls (follow-up: 50 to 100 ms; my choice inside it).</summary>
        public const double HangoverMilliseconds = 80;
        /// <summary>The fast noise policy only after this much consecutive not-voice (follow-up, section 6).</summary>
        public const double NotVoiceRunMilliseconds = 100;
        /// <summary>The loudness check: a block this far above its own floor is active (the first report's gap rule, "within 6 dB of the floor").</summary>
        public const double ActiveAboveFloorDb = 6;
        /// <summary>The loudness floor is this percentile of the last 1.5 s of blocks (as the shifter's own).</summary>
        public const int FloorPercentile = 10;

        private const int Frame = RnnoiseVoiceDetector.Frame;

        /// <summary>The frames in one classified block: 480, 10 ms at 48 kHz.</summary>
        public const int BlockFrames = Frame;
        private readonly RnnoiseVoiceDetector? _detector;
        private readonly float[] _mono = new float[Frame];
        private int _fill;
        private double _energy;
        private readonly RxContentClass[] _history = new RxContentClass[256];
        private long _blocks;
        private int _hangLeft, _notVoiceBlocks;
        private readonly int _hangBlocks, _runBlocks;
        private readonly float[] _floorRing = new float[150], _floorScratch = new float[150];
        private int _floorCount, _floorAt;
        private int _modeFlags;
        private readonly Stopwatch? _cost;

        /// <summary>The last block's voice probability, 0 to 1 (0 with no detector).</summary>
        public float LastProbability { get; private set; }

        /// <summary>Blocks classified since construction.</summary>
        public long Blocks => _blocks;

        /// <summary>True when the detector loaded; false means every block is unclassified.</summary>
        public bool DetectorAvailable => _detector?.IsAvailable == true;

        /// <summary>Why the detector is not available, or "".</summary>
        public string UnavailableDetail => _detector?.UnavailabilityDetail ?? "no detector";

        /// <summary>Tests only: time spent in the detector, when constructed with timing on.</summary>
        internal TimeSpan DetectorTime => _cost?.Elapsed ?? TimeSpan.Zero;

        public RxContentClassifier(string? libraryPath = null, bool timeDetector = false)
        {
            _hangBlocks = (int)Math.Round(HangoverMilliseconds / 10);
            _runBlocks = (int)Math.Round(NotVoiceRunMilliseconds / 10);
            try { _detector = new RnnoiseVoiceDetector(libraryPath); }
            catch (Exception ex) { Trace.WriteLine("RxContentClassifier: no voice detector: " + ex.Message); _detector = null; }
            if (_detector != null && !_detector.IsAvailable)
                Trace.WriteLine("RxContentClassifier: the voice detector is unavailable, so receive audio is unclassified and the lag shifter treats it as voice: " + _detector.UnavailabilityDetail);
            if (timeDetector) _cost = new Stopwatch();
        }

        /// <summary>
        /// The slice mode, as <c>RxAudioPipeline.SetCurrentMode</c> receives it:
        /// DIGU, DIGL and RTTY are no-splice; CW and CWL are CW (the follow-up,
        /// section 3; FDV, FM and AM are voice modes here). Any thread.
        /// </summary>
        public void SetMode(string? mode)
        {
            string m = (mode ?? "").Trim().ToUpperInvariant();
            int flags = 0;
            if (m == "DIGU" || m == "DIGL" || m == "RTTY") flags |= (int)RxContentClass.NoSplice;
            if (m == "CW" || m == "CWL" || m == "CWU") flags |= (int)RxContentClass.Cw;
            System.Threading.Volatile.Write(ref _modeFlags, flags);
        }

        /// <summary>
        /// Classify interleaved audio, block by block (480 frames, 10 ms), and
        /// return the class of the last block completed (or of the last one
        /// before, when this buffer completed none).
        /// </summary>
        public RxContentClass Classify(ReadOnlySpan<float> interleaved, int channels)
        {
            channels = Math.Max(1, channels);
            int frames = interleaved.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
                float v = sum / channels;
                _mono[_fill++] = v;
                _energy += (double)v * v;
                if (_fill == Frame) CompleteBlock();
            }
            return ClassOfBlock(_blocks - 1);
        }

        /// <summary>
        /// The class of block <paramref name="block"/> (0 is the first ever), or
        /// unclassified when it is older than the history kept or not yet seen.
        /// Used to describe audio that comes out of a delay later than it went in.
        /// </summary>
        public RxContentClass ClassOfBlock(long block)
        {
            int modeOnly = System.Threading.Volatile.Read(ref _modeFlags);
            if (block < 0 || block >= _blocks || _blocks - block > _history.Length) return (RxContentClass)modeOnly;
            return _history[block % _history.Length];
        }

        private void CompleteBlock()
        {
            _fill = 0;
            float e = (float)(_energy / Frame);
            _energy = 0;
            _floorRing[_floorAt] = e;
            _floorAt = (_floorAt + 1) % _floorRing.Length;
            if (_floorCount < _floorRing.Length) _floorCount++;
            for (int i = 0; i < _floorCount; i++) _floorScratch[i] = _floorRing[i];
            var span = new Span<float>(_floorScratch, 0, _floorCount);
            span.Sort();
            float floor = span[Math.Min(_floorCount - 1, _floorCount * FloorPercentile / 100)];
            bool active = e > Math.Max(floor * (float)Math.Pow(10, ActiveAboveFloorDb / 10), 1e-10f);

            var cls = (RxContentClass)System.Threading.Volatile.Read(ref _modeFlags);
            if (_detector?.IsAvailable == true)
            {
                _cost?.Start();
                float p = _detector.Process(_mono);
                _cost?.Stop();
                LastProbability = p;
                if (p >= SpeechThreshold) _hangLeft = _hangBlocks;
                bool speech = p >= SpeechThreshold || _hangLeft > 0;
                if (p < SpeechThreshold && _hangLeft > 0) _hangLeft--;
                cls |= RxContentClass.Classified;
                if (speech) cls |= RxContentClass.Speech;
                if (active) cls |= RxContentClass.Active;
                if (!speech && p >= UncertainThreshold) cls |= RxContentClass.Uncertain;
                if (speech || active) _notVoiceBlocks = 0; else _notVoiceBlocks++;
                if (_notVoiceBlocks >= _runBlocks) cls |= RxContentClass.NotVoiceRun;
            }
            _history[_blocks % _history.Length] = cls;
            _blocks++;
        }

        public void Dispose() => _detector?.Dispose();
    }
}
