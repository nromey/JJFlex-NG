using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Every per-sound loudness trim the picker offers reaches its sound, and a
/// trim set with no radio connected is saved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The picker (Settings, Notifications, Per-Sound Loudness) lists
/// <see cref="EarconCatalog"/>'s entries and stores each trim under the
/// entry's id; the sound reads it back under whatever id it arms. Seven
/// entries never matched: the four filter-edge trims were stored under dotted
/// ids ("FilterEdgeMoveTone.Low") while the sound armed the bare method name,
/// and the plain beep and both chirps armed nothing, so they played at
/// whatever the previous sound had left. The transmit test-tone monitor
/// skipped the trim altogether.
/// </para>
/// <para>
/// <b>Windowless and silent.</b> MTA thread throughout. Every sound is played
/// into the offline render's private mixer or the test mixer seam, so no
/// device opens, and this assembly's module initializer has render off
/// besides (<c>Infrastructure/QuietRun</c>). The persistence test writes a
/// temporary folder only.
/// </para>
/// </remarks>
public sealed class EarconTrimReachTests : IDisposable
{
    private const float TrimDb = -12f;
    private static readonly double Expected = Math.Pow(10.0, TrimDb / 20.0);   // about 0.251

    private readonly ITestOutputHelper _out;
    private readonly IReadOnlyDictionary<string, float> _savedTrims;

    public EarconTrimReachTests(ITestOutputHelper output)
    {
        _out = output;
        _savedTrims = EarconPlayer.GetAllLevelTrimsDb();
        EarconPlayer.SetAllLevelTrimsDb(null);
    }

    public void Dispose() => EarconPlayer.SetAllLevelTrimsDb(_savedTrims);

    [Fact]
    public void Every_trim_the_picker_offers_reaches_its_sound()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var missed = new List<string>();
        int checkedCount = 0;
        foreach (var e in EarconCatalog.All.Where(e => !e.IsContinuous))
        {
            EarconPlayer.SetAllLevelTrimsDb(null);
            double plain = Rms(EarconPlayer.RenderOffline(e.Play));
            if (plain <= 1e-6) { _out.WriteLine($"{e.Id}: silent here, not judged"); continue; }

            EarconPlayer.SetLevelTrimDb(e.Id, TrimDb);
            double trimmed = Rms(EarconPlayer.RenderOffline(e.Play));
            double ratio = trimmed / plain;
            checkedCount++;
            // The two sweeps carry clock-seeded noise, so their level varies a
            // little from play to play; everything else renders identically.
            if (Math.Abs(ratio - Expected) > 0.05)
                missed.Add($"{e.Id} (level ratio {ratio:F3}, expected {Expected:F3})");
        }
        _out.WriteLine($"judged {checkedCount} sounds");
        Assert.True(checkedCount >= 40, $"only {checkedCount} sounds judged, so this proves little");
        Assert.True(missed.Count == 0, "Trims that do not reach their sound: " + string.Join("; ", missed));
    }

    [Fact]
    public void An_ungated_sound_does_not_inherit_the_trim_the_previous_sound_left()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // An untrimmed gated sound first, so the reference beep is measured
        // with no trim left on this thread by an earlier test.
        EarconPlayer.RenderOffline(EarconPlayer.ConfirmTone);
        double plain = Rms(EarconPlayer.RenderOffline(() => EarconPlayer.Beep()));
        Assert.True(plain > 1e-6, "the beep rendered nothing, so the test would prove nothing");

        // A heavily trimmed sound first, on the same thread, then the beep.
        EarconPlayer.SetLevelTrimDb(nameof(EarconPlayer.ConfirmTone), -30f);
        EarconPlayer.RenderOffline(EarconPlayer.ConfirmTone);
        double after = Rms(EarconPlayer.RenderOffline(() => EarconPlayer.Beep()));

        Assert.InRange(after / plain, 0.99, 1.01);
    }

    [Fact]
    public void Every_continuous_sound_the_picker_offers_plays_at_its_trim()
    {
        // The one-shot test above leaves out the continuous entries, which
        // need Start and Stop: the transmit test-tone monitor and the ATU
        // progress tone. Both are in the picker, so both must answer it.
        NoiseReductionPerChannelTests.AssertWindowless();
        var continuous = EarconCatalog.All.Where(e => e.IsContinuous).ToList();
        Assert.Contains(continuous, e => e.Id == EarconPlayer.TxToneMonitorTrimId);
        Assert.Contains(continuous, e => e.Id == nameof(EarconPlayer.StartATUProgressEarcon));

        var missed = new List<string>();
        foreach (var e in continuous)
        {
            EarconPlayer.SetAllLevelTrimsDb(null);
            double plain = ContinuousRms(e);
            Assert.True(plain > 1e-6, e.Id + " submitted nothing, so the test would prove nothing");

            EarconPlayer.SetLevelTrimDb(e.Id, TrimDb);
            double ratio = ContinuousRms(e) / plain;
            if (Math.Abs(ratio - Expected) > 0.02)
                missed.Add($"{e.Id} (level ratio {ratio:F3}, expected {Expected:F3})");
        }
        Assert.True(missed.Count == 0, "Continuous sounds that ignore their trim: " + string.Join("; ", missed));
    }

    [Fact]
    public void A_figure_played_later_on_a_worker_thread_keeps_its_sounds_trim()
    {
        // The connect-sound audition A plays its last figure later, from a
        // pool thread, where the thread-static trim is whatever that thread
        // last held. The figure must carry the trim of the sound it belongs
        // to. The offline render places the figure on the calling thread, so
        // only the live path, through the test mixer seam, can see this.
        NoiseReductionPerChannelTests.AssertWindowless();
        const string id = "ConnectSeriesCandidate.A";
        Assert.Contains(EarconCatalog.All, e => e.Id == id);

        double plain = LaterFigureRms(() => EarconPlayer.ConnectSeriesCandidate('A'));
        Assert.True(plain > 1e-6, "the later figure never arrived, so the test would prove nothing");

        EarconPlayer.SetLevelTrimDb(id, TrimDb);
        double trimmed = LaterFigureRms(() => EarconPlayer.ConnectSeriesCandidate('A'));

        Assert.InRange(trimmed / plain, Expected - 0.02, Expected + 0.02);
    }

    [Fact]
    public void A_trim_set_with_no_radio_connected_survives_a_save_and_reload()
    {
        // With no radio connected, Settings OK persists only the user-scope
        // list (AudioOutputConfig.PersistUserScope), and the trims and the
        // receive duck were not on it.
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-trims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig><CwSpeedWpm>25</CwSpeedWpm></AudioOutputConfig>");

            var chosen = AudioOutputConfig.Load(dir);
            chosen.EarconLevelTrims = new List<AudioOutputConfig.EarconLevelTrim>
            {
                new() { Id = EarconPlayer.FilterEdgeMoveLowTrimId, Db = -6f },
                new() { Id = nameof(EarconPlayer.Beep), Db = 3f },
            };
            chosen.RxDuckEnabled = true;
            chosen.RxDuckDepthDb = -9f;
            chosen.RxDuckTiming = "Lingering";
            AudioOutputConfig.PersistUserScope(chosen, dir);

            var reopened = AudioOutputConfig.Load(dir);
            Assert.Equal(
                new[] { (EarconPlayer.FilterEdgeMoveLowTrimId, -6f), (nameof(EarconPlayer.Beep), 3f) },
                reopened.EarconLevelTrims.Select(t => (t.Id, t.Db)).ToArray());
            Assert.True(reopened.RxDuckEnabled);
            Assert.Equal(-9f, reopened.RxDuckDepthDb);
            Assert.Equal("Lingering", reopened.RxDuckTiming);
            Assert.Equal(25, reopened.CwSpeedWpm);   // the rest of the root file is left as it was
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Start a continuous sound into the test mixer, read a second of it, stop it.</summary>
    private static double ContinuousRms(EarconEntry e)
    {
        var mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 2)) { ReadFully = false };
        EarconPlayer.TestAlertMixer = mixer;
        try
        {
            e.Play();
            var buffer = new float[EarconPlayer.MixerSampleRate * 2];
            int n = mixer.Read(buffer.AsSpan());
            e.Stop!();
            return Rms(buffer.Take(n).ToArray());
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
        }
    }

    /// <summary>
    /// Play a sound into the test mixer, drop what it added at once, wait for
    /// the figure it schedules for later, and measure that figure alone.
    /// </summary>
    private static double LaterFigureRms(Action play)
    {
        var mixer = new MixingSampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(EarconPlayer.MixerSampleRate, 2)) { ReadFully = false };
        EarconPlayer.TestAlertMixer = mixer;
        try
        {
            play();
            foreach (var now in mixer.MixerInputs.ToList()) mixer.RemoveMixerInput(now);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!mixer.MixerInputs.Any() && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Assert.True(mixer.MixerInputs.Any(), "no figure arrived within five seconds");

            var all = new List<float>();
            var buffer = new float[EarconPlayer.MixerSampleRate / 10 * 2];
            for (int guard = 0; guard < 100; guard++)
            {
                int n = mixer.Read(buffer.AsSpan());
                if (n <= 0) break;
                all.AddRange(buffer.Take(n));
            }
            return Rms(all.ToArray());
        }
        finally
        {
            EarconPlayer.TestAlertMixer = null;
        }
    }

    private static double Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (float s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }
}
