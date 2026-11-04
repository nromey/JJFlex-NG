using System;
using System.Collections.Generic;
using System.Globalization;
using Flex.Smoothlake.FlexLib;
using Radios.Listenback;

namespace Radios;

public partial class FlexBase
{
    /// <summary>The computer's microphone device feeding PC audio, named as the off-air take's header names it.</summary>
    public string PcMicrophoneDeviceName => remoteInputDevice?.Name ?? "unavailable";

    /// <summary>
    /// What fed the transmitter right now, in the take header's words: the
    /// test tone, the reference recording, or the voice. The tone wins when
    /// both read engaged, as <see cref="TakeSetup.Source"/> reads an IQ take.
    /// Ask at the keying edge, never at the later playback.
    /// </summary>
    public string PlaybackTakeSource => TxToneEngaged ? TakeSetup.Tone : TxFilePlaying ? TakeSetup.Reference : TakeSetup.Voice;

    /// <summary>The facts the "start a new test or add to this one" question compares (Q7, D5).</summary>
    public TakeAdmissionFacts TakeAdmissionFactsNow() =>
        new(theRadio?.Serial ?? SelectedRadioSerial ?? "", CurrentMicProfileName ?? "", MicSource ?? "", PcMicrophoneDeviceName);

    /// <summary>
    /// The facts a playback take records about the recording, captured at
    /// RECORDING admission (Unified takes, design sections 1 and 7, part 4b):
    /// the transmit chain as it was when the radio recorded the content, not
    /// at the later playback, so yesterday's slot never claims today's profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set round one listed: <see cref="TakeSetup.Snapshot"/> (never
    /// <c>ApplyTo</c>; nothing here touches the radio); model, serial,
    /// nickname, firmware and transport; mode, tx-frequency-mhz, tx-antenna,
    /// receive-antenna and dummy-load; slice-audio-gain; the provenance tokens
    /// with <c>boundary-basis=status-arrival</c> and
    /// <c>boundary-quality=unverified</c>. The source is written separately, at
    /// the keying edge (<see cref="PlaybackTakeSource"/>).
    /// </para>
    /// <para>
    /// Added beside that set, each read by something: the recording's identity
    /// (check method, slice index and letter, connection) for the bench
    /// measurement and the trace; and for Looped back the listening receiver's
    /// RF gain, AGC and filter, which <c>LevelWithheld</c> compares before it
    /// will ever compare loudness between two Looped back takes.
    /// </para>
    /// <para>
    /// No IQ fact is invented: no <c>rate-echoed</c>, <c>pan-centre-mhz</c> or
    /// <c>render-scale</c>. A value the radio has not given is left out, never
    /// guessed.
    /// </para>
    /// </remarks>
    /// <param name="kind"><see cref="TakeKind.RadioRecording"/> or <see cref="TakeKind.LoopedBack"/>.</param>
    /// <param name="recorded">The slice that records, and later plays, the take.</param>
    /// <param name="method">The check method, as a token (<c>record-playback</c> or <c>loopback</c>).</param>
    public IReadOnlyList<KeyValuePair<string, string>> PlaybackTakeFacts(string kind, SliceRecorderTarget recorded, string method)
    {
        var radio = theRadio;
        var facts = new List<KeyValuePair<string, string>>();
        void Add(string key, string value) { if (value != null) facts.Add(new(key, value)); }
        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        Add("check-method", method);
        Add("model", radio?.Model ?? RadioModel);
        Add("serial", radio?.Serial ?? SelectedRadioSerial);
        Add("nickname", radio?.Nickname ?? "");
        if (radio != null) Add("firmware", Flex.Util.FlexVersion.ToString(radio.Version));
        Add("transport", RemoteRig ? "SmartLink" : "LAN");
        Add("connection-id", radio?.CurrentCommandConnection?.Id.ToString() ?? "");
        Add("application", typeof(FlexBase).Assembly.GetName().Version?.ToString() ?? "unknown");

        var tx = SliceForTransmitSetting();
        Slice slice = ResolveSliceRecorderTarget(recorded);
        if (slice != null)
        {
            Add("recorded-slice-index", slice.Index.ToString(CultureInfo.InvariantCulture));
            Add("recorded-slice-letter", LetterOf(slice));
        }
        Add("mode", tx?.DemodMode);
        if (tx != null) Add("tx-frequency-mhz", Number(tx.Freq));
        Add("tx-antenna", tx?.TXAnt);
        Add("receive-antenna", slice?.RXAnt);
        Add("dummy-load", DummyLoadMode.ToString());
        if (slice != null) Add("slice-audio-gain", slice.AudioGain.ToString(CultureInfo.InvariantCulture));
        if (kind == TakeKind.LoopedBack && slice != null)
        {
            Add("rf-gain", slice.RFGain.ToString(CultureInfo.InvariantCulture));
            Add("agc-mode", slice.AGCMode.ToString());
            Add("agc-threshold", slice.AGCThreshold.ToString(CultureInfo.InvariantCulture));
            Add("rx-filter-low", slice.FilterLow.ToString(CultureInfo.InvariantCulture));
            Add("rx-filter-high", slice.FilterHigh.ToString(CultureInfo.InvariantCulture));
        }

        // The transmit chain, through the one registry (design section 7).
        string device = PcMicrophoneDeviceName;
        int power = radio?.RFPower ?? XmitPower;
        foreach (var fact in TakeSetup.Snapshot(new TakeSetup.SetupSource(
                     AudioChainPreset.CaptureFrom(this, "playback take", device), CurrentMicProfileName ?? "", power, device)))
            facts.Add(fact);
        // The tone and reference as at admission; the keying edge's source wins.
        Add("tone-engaged", TxToneEngaged.ToString());
        Add("tone-hz", TxToneFrequency.ToString(CultureInfo.InvariantCulture));
        Add("reference-audio", TxFilePlaying.ToString());

        // Provenance: what this audio is, said once as schema tokens (design section 1).
        Add(AudioTakeFields.CaptureTap, "pre-pc-output-processing");
        Add(AudioTakeFields.WireCodec, "opus");
        Add(AudioTakeFields.Provenance, "radio-playback-after-network-codec");
        Add(AudioTakeFields.CaptureScope, "radio-receive-mix");
        Add(AudioTakeFields.BoundaryBasis, "status-arrival");
        Add(AudioTakeFields.BoundaryQuality, AudioTakeFields.Unverified);
        return facts;
    }
}
