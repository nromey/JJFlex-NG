#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios.Listenback;

namespace Radios.Tests;

/// <summary>
/// Created-state fixtures for Unified takes: playback takes written by the
/// real <see cref="AudioTakeWriter"/>, and minimal IQ headers for the catalog.
/// Every folder lives under a fresh temporary root that the test deletes.
/// </summary>
internal static class UnifiedTakeFixtures
{
    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "jjflex-unified-" + Guid.NewGuid().ToString("N"));

    public static void Remove(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    /// <summary>The facts a playback take records about itself and its radio, overridable.</summary>
    public static Dictionary<string, string?> BaseFacts(string serial = "same-radio") => new()
    {
        ["model"] = "FLEX-8600", ["serial"] = serial, ["nickname"] = "Bench",
        ["firmware"] = "4.2.20", ["transport"] = "LAN",
        ["mode"] = "USB", ["tx-frequency-mhz"] = "14.2", ["tx-antenna"] = "XVTA",
        ["rf-power"] = "10", ["dummy-load"] = "False",
        ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000",
        ["mic-input"] = "PC", ["pc-microphone-device"] = "Yeti", ["mic-profile"] = "Default",
        ["mic-gain"] = "50", ["compander"] = "off", ["compander-level"] = "50",
        ["processor"] = "False", ["processor-level"] = "0",
        ["source"] = TakeSetup.Voice,
        ["slice-audio-gain"] = "50",
        ["receive-antenna"] = "XVTB",
        // Round two: what else reached the capture (D2); only "isolated" is ever measured against another take.
        ["capture-isolation"] = "isolated",
        [AudioTakeFields.CaptureTap] = "pre-pc-output-processing",
        [AudioTakeFields.WireCodec] = "opus",
        [AudioTakeFields.Provenance] = "radio-playback-after-network-codec",
        [AudioTakeFields.CaptureScope] = "radio-receive-mix",
        [AudioTakeFields.BoundaryBasis] = "status-arrival",
        [AudioTakeFields.BoundaryQuality] = AudioTakeFields.Unverified,
    };

    /// <summary>
    /// Speech-like audio: many partials with a slowly varying envelope, low
    /// and high bands weighted separately so tone can be told apart.
    /// </summary>
    public static float[] Speechlike(int seed, int samples, double lowGain = 1, double highGain = 1, double level = 0.2)
    {
        var random = new Random(seed);
        int rate = AudioTakeFields.Rate;
        double[] frequencies = new double[60];
        for (int t = 0; t < frequencies.Length; t++)
            frequencies[t] = t < 20 ? 140 + t * 21.3 : 1200 + (t - 20) * 41.7;
        double[] phases = new double[frequencies.Length], envelopes = new double[frequencies.Length];
        for (int t = 0; t < frequencies.Length; t++) phases[t] = random.NextDouble() * 2 * Math.PI;
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            if (i % 720 == 0)
                for (int t = 0; t < frequencies.Length; t++) envelopes[t] = 0.45 + random.NextDouble() * 0.55;
            double value = 0;
            for (int t = 0; t < frequencies.Length; t++)
            {
                double gain = t < 20 ? lowGain / Math.Sqrt(20) : highGain / Math.Sqrt(40);
                value += gain * envelopes[t] * Math.Sin(phases[t] + 2 * Math.PI * frequencies[t] * i / rate);
            }
            result[i] = (float)(level * value);
        }
        return result;
    }

    /// <summary>
    /// Write a playback take through the real writer: a quarter second before
    /// playback, <paramref name="seconds"/> of playback, and a quarter second
    /// after. <paramref name="facts"/> overrides the base facts; a null value
    /// leaves the fact out.
    /// </summary>
    public static string AudioTake(string root, string kind = TakeKind.RadioRecording, int seed = 7,
        double seconds = 2, double lowGain = 1, double highGain = 1, double level = 0.2,
        IDictionary<string, string?>? facts = null, bool clean = true, string? name = null,
        Action<AudioTakeWriter>? during = null)
    {
        var written = BaseFacts();
        if (facts != null) foreach (var fact in facts) written[fact.Key] = fact.Value;
        var list = new List<KeyValuePair<string, string>>();
        foreach (var fact in written) if (fact.Value != null) list.Add(new(fact.Key, fact.Value));
        string directory = name == null ? AudioTakeWriter.NewDirectory(root) : Path.Combine(root, name);
        var writer = new AudioTakeWriter(directory, kind, list);
        int rate = AudioTakeFields.Rate;
        var lead = new float[rate / 4];
        writer.Write(lead, lead.Length);
        writer.BeginPlayback();
        var body = Speechlike(seed, (int)(rate * seconds), lowGain, highGain, level);
        writer.Write(body, body.Length);
        during?.Invoke(writer);
        writer.EndPlayback();
        writer.Write(lead, lead.Length);
        writer.Close(clean, clean ? "playback-ended" : "stop-not-observed");
        return directory;
    }

    /// <summary>
    /// What a crash mid-capture leaves: a header with no closing block and a
    /// WAV whose reserved header was never patched (all zeros), as
    /// <c>WavWriter</c> leaves it until <c>Finish</c>.
    /// </summary>
    public static string UnclosedAudioTake(string root)
    {
        string directory = AudioTakeWriter.NewDirectory(root);
        Directory.CreateDirectory(directory);
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt"), TakeKind.AudioFormat))
        {
            header.Write("kind", TakeKind.RadioRecording);
            header.Write(AudioTakeFields.AudioFile, AudioTakeFields.WavName);
            header.Write(AudioTakeFields.AudioRate, AudioTakeFields.Rate);
            header.Write(AudioTakeFields.AudioChannels, 1);
            header.Write(AudioTakeFields.AudioEncoding, AudioTakeFields.Encoding);
        }
        var bytes = new byte[44 + 4800];
        File.WriteAllBytes(Path.Combine(directory, AudioTakeFields.WavName), bytes);
        return directory;
    }

    /// <summary>
    /// A complete, playable IQ take of <paramref name="kind"/> (the shape the
    /// listen-back comparison tests use): 8 kHz IQ of speech-like partials,
    /// keyed throughout, USB, closed clean. Its render is real.
    /// </summary>
    public static string IqTake(string root, string kind, int seed = 7, string serial = "same-radio",
        double lowGain = 1, double highGain = 1, IDictionary<string, string?>? facts = null)
    {
        const int rate = 8000, samples = rate * 3;
        string directory = Path.Combine(root, "iq-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var random = new Random(seed);
        double[] frequencies = new double[80];
        for (int t = 0; t < frequencies.Length; t++) frequencies[t] = t < 20 ? 140 + t * 21.3 : 1200 + (t - 20) * 21.7;
        double[] phases = new double[frequencies.Length], envelopes = new double[frequencies.Length];
        for (int t = 0; t < frequencies.Length; t++) phases[t] = random.NextDouble() * 2 * Math.PI;
        string iq = Path.Combine(directory, "rf.iq");
        using (var writer = new BinaryWriter(File.Create(iq)))
            for (int i = 0; i < samples; i++)
            {
                if (i % 240 == 0) for (int t = 0; t < frequencies.Length; t++) envelopes[t] = 0.45 + random.NextDouble() * 0.55;
                double re = 0, im = 0;
                for (int t = 0; t < frequencies.Length; t++)
                {
                    double gain = t < 20 ? lowGain / Math.Sqrt(20) : highGain / Math.Sqrt(60);
                    double phase = phases[t] + 2 * Math.PI * frequencies[t] * i / rate;
                    re += gain * envelopes[t] * Math.Cos(phase); im += gain * envelopes[t] * Math.Sin(phase);
                }
                writer.Write((float)(32768 * 0.025 * re)); writer.Write((float)(32768 * 0.025 * im));
            }
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt")))
        {
            var written = new Dictionary<string, string?>
            {
                ["kind"] = kind, ["serial"] = serial, ["model"] = "FLEX-8600", ["nickname"] = "Bench", ["transport"] = "LAN",
                ["mode"] = "USB", ["rate-echoed"] = rate.ToString(), ["rate-requested"] = rate.ToString(),
                ["receive-antenna"] = "XVTB", ["rf-gain"] = "32", ["tx-antenna"] = "XVTA", ["rf-power"] = "10",
                ["tx-frequency-mhz"] = "14.2", ["pan-centre-mhz"] = "14.2", ["tx-filter-low"] = "100", ["tx-filter-high"] = "3000",
                ["dummy-load"] = "False", ["mic-input"] = "PC", ["pc-microphone-device"] = "Yeti", ["mic-profile"] = "Default",
                ["reference-audio"] = "False", ["render-scale"] = "1/32768; fixed; no normalization; no AGC", ["classifier"] = "noise-like",
            };
            if (facts != null) foreach (var fact in facts) written[fact.Key] = fact.Value;
            foreach (var fact in written) if (fact.Value != null) header.Write(fact.Key, fact.Value);
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = 0 });
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = false, AtPair = samples });
            header.Write("pairs-written", samples);
            header.Write("sha256", TakeHeader.Checksum(iq));
            header.Write("closed", "clean");
        }
        return directory;
    }

    /// <summary>A minimal IQ header, for the catalog: no rf.iq unless asked.</summary>
    public static string IqHeader(string root, string name, string? kind, bool heardWav = true)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt")))
        {
            if (kind != null) header.Write("kind", kind);
            header.Write("serial", "same-radio");
            header.Write("closed", "clean");
        }
        if (heardWav) File.WriteAllBytes(Path.Combine(directory, "heard.wav"), new byte[] { 0 });
        return directory;
    }
}
