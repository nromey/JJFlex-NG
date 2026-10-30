using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// #855 step 5: the frequent sounds are made once and played from memory, and
/// what plays from memory is the same sound, sample for sample, as what would
/// have been made fresh.
/// </summary>
/// <remarks>
/// <para><b>Windowless and silent.</b> MTA thread throughout; every sound is
/// played only into the offline render's private mixer, so no device opens.</para>
/// <para><b>The comparison is exact.</b> Not "close": the held samples are the
/// render's own output, so any difference at all is a defect in the key.</para>
/// </remarks>
public sealed class PrerenderedSoundsTests
{
    private readonly ITestOutputHelper _out;
    public PrerenderedSoundsTests(ITestOutputHelper output) { _out = output; }

    private static float[] Fresh(Action play)
    {
        bool saved = EarconPlayer.SoundCacheEnabled;
        EarconPlayer.SoundCacheEnabled = false;
        try { return EarconPlayer.RenderOffline(play); }
        finally { EarconPlayer.SoundCacheEnabled = saved; }
    }

    [Fact]
    public void Every_sound_from_memory_is_the_sound_made_fresh_and_the_frequent_ones_do_come_from_memory()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(EarconPlayer.SoundCacheEnabled);
        EarconPlayer.Prewarm();
        Assert.True(EarconPlayer.HeldSoundCount >= 25, $"only {EarconPlayer.HeldSoundCount} held");

        int compared = 0;
        var varying = new List<string>();
        foreach (var e in EarconCatalog.All.Where(e => !e.IsContinuous))
        {
            var a = Fresh(e.Play);
            var b = Fresh(e.Play);
            if (!a.SequenceEqual(b)) { varying.Add(e.Id); continue; }   // noise seeded from the clock: never held
            var held = EarconPlayer.RenderOffline(e.Play);
            Assert.True(a.SequenceEqual(held), $"{e.Id}: the sound from memory differs from the sound made fresh");
            compared++;
        }
        _out.WriteLine($"compared {compared}; different on every play, so never held: {string.Join(", ", varying)}");
        Assert.True(compared >= 40, $"only {compared} sounds compared");
        // Only the sweeps carry clock-seeded noise; anything else varying is a new defect.
        Assert.All(varying, id => Assert.Contains(id, new[] { "PlayExpand", "PlayCollapse" }));

        // And the warm set really is played from memory, not merely equal.
        foreach (var play in new Action[] { EarconPlayer.TxStartTone, EarconPlayer.DialogOpenTone,
                     EarconPlayer.LeaderEnterTone, () => EarconPlayer.ConnectPhaseTone(2) })
        {
            long before = EarconPlayer.HeldHits;
            EarconPlayer.RenderOffline(play);
            Assert.True(EarconPlayer.HeldHits > before, "a warm-set sound was made again instead of played from memory");
        }
    }

    [Fact]
    public void A_changed_countdown_is_held_at_its_new_settings_after_the_next_warm_up()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var original = CountdownSettings.Current;
        try
        {
            CountdownSettings.Current = new CountdownSettings { PipHz = 500, PipMs = 150, PipSound = "Bell" };
            var s = CountdownSettings.Current;
            Action pip = () => EarconPlayer.CountdownBeatTone(s.PipHz, s.PipMs, false, s.PipSound);
            Action landing = () => EarconPlayer.CountdownBeatTone(s.PipHz * 2, s.LandingMs, true, s.PipSound);

            EarconPlayer.Prewarm();
            foreach (var play in new[] { pip, landing, (Action)EarconPlayer.CountdownTransmitTone })
            {
                var fresh = Fresh(play);
                long before = EarconPlayer.HeldHits;
                var held = EarconPlayer.RenderOffline(play);
                Assert.True(EarconPlayer.HeldHits > before, "the countdown at its new settings was not held");
                Assert.True(fresh.SequenceEqual(held));
            }
        }
        finally
        {
            CountdownSettings.Current = original;
            EarconPlayer.Prewarm();
        }
    }

    [Fact]
    public void A_sound_held_under_one_alert_tone_set_is_not_played_under_the_other()
    {
        // The decaying shape reads the alert tone set, not only the voice, so
        // the set is part of every key. Without it a pip held under Rich would
        // play under Simple.
        NoiseReductionPerChannelTests.AssertWindowless();
        var saved = EarconVoices.ActiveSet;
        try
        {
            EarconVoices.ActiveSet = EarconVoiceSet.Rich;
            EarconPlayer.Prewarm();
            EarconVoices.ActiveSet = EarconVoiceSet.Simple;
            foreach (var play in new Action[] { EarconPlayer.TxStartTone, EarconPlayer.CountdownTransmitTone,
                         EarconPlayer.FixerStageDoneTone, EarconPlayer.DingTone })
                Assert.True(Fresh(play).SequenceEqual(EarconPlayer.RenderOffline(play)));
        }
        finally
        {
            EarconVoices.ActiveSet = saved;
            EarconPlayer.Prewarm();
        }
    }

    [Fact]
    public void A_voice_key_changes_with_every_property_a_render_could_read_and_noise_is_never_held()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var v = EarconVoices.Chime.Clone();
        string k0 = EarconPlayer.VoiceKey(v)!;
        Assert.Equal(k0, EarconPlayer.VoiceKey(v.Clone()));

        foreach (var change in new Action<MeterVoice>[]
                 {
                     m => m.Brightness += 0.1f, m => m.AttackMs += 1, m => m.DecayMs += 1,
                     m => m.SustainLevel = m.SustainLevel == 0.5f ? 0.25f : 0.5f, m => m.TremoloDepth += 0.1f, m => m.VibratoRateHz += 1,
                     m => m.GateOnMs += 5, m => m.Inharmonicity += 0.01f,
                     m => m.Partials = m.Partials.Append(0.1f).ToArray(),
                 })
        {
            var c = v.Clone();
            change(c);
            Assert.NotEqual(k0, EarconPlayer.VoiceKey(c));
        }

        var noisy = v.Clone();
        noisy.NoiseLevel = 0.2f;
        Assert.Null(EarconPlayer.VoiceKey(noisy));
    }

    [Theory]
    [InlineData(700, 60, 5, null)]
    [InlineData(700, 180, 5, null)]
    [InlineData(550, 40, 3, "Hollow")]
    [InlineData(900, 120, 8, "Bell")]
    public void A_CW_mark_from_memory_is_the_mark_the_tone_generator_makes(int hz, int ms, int rf, string? waveform)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var voice = waveform == null ? null : EarconVoices.ResolveCwWaveform(waveform).Voice;
        float[] Read(ISampleProvider p)
        {
            var all = new List<float>();
            var buf = new float[1000];   // a block size that does not divide the mark, on purpose
            int n;
            while ((n = p.Read(buf)) > 0) all.AddRange(buf.Take(n));
            return all.ToArray();
        }
        var made = Read(new CwToneSampleProvider(EarconPlayer.MixerSampleRate, hz, ms, rf, 0.4f, voice));
        var first = Read(EarconPlayer.CwMark(EarconPlayer.MixerSampleRate, hz, ms, rf, 0.4f, voice));
        var again = Read(EarconPlayer.CwMark(EarconPlayer.MixerSampleRate, hz, ms, rf, 0.4f, voice));
        Assert.True(made.Length > 0);
        Assert.True(made.SequenceEqual(first));
        Assert.True(made.SequenceEqual(again));
    }
}
