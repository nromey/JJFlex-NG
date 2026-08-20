using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// Who a piece of radio state belongs to. This is the classification the
    /// sprint asked for; it did not exist anywhere in the project before.
    /// </summary>
    public enum StateOwnership
    {
        /// <summary>
        /// One value for the whole station. Every connected client sees the same
        /// number and any client that writes it changes it for everyone.
        /// Transmit power, mic gain, mic source, the ATU, the interlock.
        /// <para>Reading this from our own connection is legitimate and means
        /// exactly what it appears to mean. Writing it while another operator is
        /// connected is not acceptable.</para>
        /// </summary>
        StationGlobal,

        /// <summary>
        /// Lives on an object that carries a client handle — a slice, a
        /// panadapter, an audio stream.
        /// <para>The important and slightly counter-intuitive part: this state is
        /// globally OBSERVABLE and privately OWNED. Any client can read slice
        /// 3's mode; only slice 3's owner should write it. So an observer CAN
        /// honestly verify the application's slices, provided it attributes them
        /// by handle. What it must never do is create its own slice and assert
        /// on that, which is inspecting its own reflection.</para>
        /// </summary>
        ClientOwned,

        /// <summary>
        /// The radio reporting on itself: meters, interlock state, ATU tune
        /// result, capability counts. Not settable, so never restored and never
        /// exercised — only observed.
        /// </summary>
        Telemetry,

        /// <summary>Not classified. Never written by the harness.</summary>
        Unknown,
    }

    /// <summary>
    /// How much we actually know about an entry.
    ///
    /// <para>The analyzer work in this sprint insists that a check which could
    /// not be made is never counted as one that passed. The same honesty applies
    /// to the harness's own metadata, so the table records where each
    /// classification came from rather than presenting guesswork and vendor
    /// source as though they were the same thing.</para>
    /// </summary>
    public enum Confidence
    {
        /// <summary>Inferred from naming and general Flex behaviour. Weakest.</summary>
        Assumed,

        /// <summary>Read out of the vendored FlexLib parser, with a line reference.</summary>
        FromVendorSource,

        /// <summary>Exercised against the bench 8600 and the radio agreed.</summary>
        VerifiedOnHardware,
    }

    /// <summary>One row of the ownership table.</summary>
    public sealed record RigFieldSpec
    {
        public required RigTarget Target { get; init; }

        /// <summary>The key exactly as the radio SPELLS IT IN STATUS.</summary>
        public required string StatusKey { get; init; }

        public required StateOwnership Ownership { get; init; }

        /// <summary>
        /// The command that writes this field, as a template. Null means there
        /// is no write path and the harness will never attempt one.
        /// <para>Placeholders: <c>{i}</c> object index, <c>{v}</c> value,
        /// <c>{x}</c> object index formatted as an 0x-prefixed 8-digit handle.</para>
        /// </summary>
        public string? SetTemplate { get; init; }

        /// <summary>
        /// For the handful of fields whose write is not a substitution — slice
        /// lock, which is two different verbs rather than a value.
        /// </summary>
        public Func<int, string, string>? Writer { get; init; }

        public required Confidence Confidence { get; init; }

        public required string Notes { get; init; }

        /// <summary>Values are uppercased before being sent, as FlexLib does.</summary>
        public bool UppercaseOnWrite { get; init; }

        public bool Writable => SetTemplate is not null || Writer is not null;
    }

    /// <summary>
    /// The station-global versus client-owned classification of radio state,
    /// plus the write path for everything the harness is allowed to change.
    ///
    /// <para><b>Why one table and not two.</b> Two separate failure modes need
    /// this information. The first is the MultiFlex trap: a harness that creates
    /// its own slice and asserts on its mode proves nothing about the
    /// application, and the failure is silent because it looks exactly like a
    /// pass. The second is restore: most of what the radio reports is telemetry,
    /// and a restore pass that tried to write back every field it snapshotted
    /// would spray nonsense at the radio. Both questions are answered by the
    /// same row.</para>
    ///
    /// <para><b>Status keys and set keys are different vocabularies.</b> That is
    /// not an occasional wrinkle, it is the norm, and every place it bites is
    /// recorded in the Notes. Mic gain reports as <c>mic_level</c> and is
    /// written as <c>miclevel</c>. Slice frequency reports as
    /// <c>RF_frequency</c> and is written with <c>slice tune</c>. Slice filter
    /// edges report as <c>filter_lo</c> and <c>filter_hi</c> and are written
    /// with <c>filt</c>. The transmit monitor reports as <c>sb_monitor</c> and
    /// is written as <c>mon</c>. A tool that assumes one vocabulary silently
    /// fails to write about a dozen fields.</para>
    ///
    /// <para><b>Read the confidence column.</b> Anything not marked
    /// <see cref="Confidence.VerifiedOnHardware"/> has not been proven against
    /// the bench 8600.</para>
    /// </summary>
    public static class OwnershipTable
    {
        private static readonly RigFieldSpec[] Specs = BuildSpecs();

        private static readonly Dictionary<(RigTarget, string), RigFieldSpec> ByKey = Specs
            .GroupBy(s => (s.Target, s.StatusKey), TargetKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.First(), TargetKeyComparer.Instance);

        public static IReadOnlyList<RigFieldSpec> All => Specs;

        /// <summary>
        /// Looks a field up. Unknown fields come back as an Unknown/not-writable
        /// spec rather than null, so every caller gets the safe answer by
        /// default: do not write it, do not assume who owns it.
        /// </summary>
        public static RigFieldSpec Lookup(RigField field)
        {
            return ByKey.TryGetValue((field.Target, field.Key), out RigFieldSpec? spec)
                ? spec
                : new RigFieldSpec
                {
                    Target = field.Target,
                    StatusKey = field.Key,
                    Ownership = StateOwnership.Unknown,
                    Confidence = Confidence.Assumed,
                    Notes = "Not in the ownership table. Treated as unknown and never written.",
                };
        }

        public static bool IsWritable(RigField field) => Lookup(field).Writable;

        public static StateOwnership OwnershipOf(RigField field) => Lookup(field).Ownership;

        /// <summary>
        /// Builds the wire command that writes this field, or null where there
        /// is no write path.
        /// </summary>
        public static string? SetCommand(RigField field, string value)
        {
            RigFieldSpec spec = Lookup(field);
            if (spec.UppercaseOnWrite) value = value.ToUpperInvariant();

            if (spec.Writer is not null) return spec.Writer(field.Index, value);
            if (spec.SetTemplate is null) return null;

            return spec.SetTemplate
                .Replace("{i}", field.Index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", "0x" + unchecked((uint)field.Index).ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{v}", StatusParser.EncodeValue(value), StringComparison.Ordinal);
        }

        /// <summary>
        /// The two pairs that must be written together or not at all.
        ///
        /// <para>Filter edges are a single command taking both values, and the
        /// vendor library additionally drops the whole command if low is not
        /// below high. Restoring them one at a time therefore has a real failure
        /// mode: the intermediate state is inverted, the radio rejects it, and
        /// the passband is left wrong with nothing reported.</para>
        /// </summary>
        public static IReadOnlyList<(RigTarget Target, string LowKey, string HighKey)> CompositePairs { get; } =
            new[]
            {
                (RigTarget.Slice, "filter_lo", "filter_hi"),
                (RigTarget.Transmit, "lo", "hi"),
            };

        public static string CompositeCommand(RigTarget target, int index, string low, string high) => target switch
        {
            RigTarget.Slice => string.Create(CultureInfo.InvariantCulture, $"filt {index} {low} {high}"),
            RigTarget.Transmit => $"transmit set filter_low={low} filter_high={high}",
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "No composite write for this object."),
        };

        // ---------------------------------------------------------------- //

        private static RigFieldSpec[] BuildSpecs()
        {
            var list = new List<RigFieldSpec>();

            void Add(RigTarget target, string key, StateOwnership ownership, string? template,
                     Confidence confidence, string notes, bool upper = false, Func<int, string, string>? writer = null)
                => list.Add(new RigFieldSpec
                {
                    Target = target,
                    StatusKey = key,
                    Ownership = ownership,
                    SetTemplate = template,
                    Writer = writer,
                    Confidence = confidence,
                    Notes = notes,
                    UppercaseOnWrite = upper,
                });

            void Slice(string key, string? template, Confidence c, string notes, bool upper = false, Func<int, string, string>? writer = null)
                => Add(RigTarget.Slice, key, StateOwnership.ClientOwned, template, c, notes, upper, writer);

            void Pan(string key, string? template, Confidence c, string notes)
                => Add(RigTarget.Display, key, StateOwnership.ClientOwned, template, c, notes);

            void Tx(string key, string? template, Confidence c, string notes, bool upper = false)
                => Add(RigTarget.Transmit, key, StateOwnership.StationGlobal, template, c, notes, upper);

            void Radio(string key, string? template, Confidence c, string notes)
                => Add(RigTarget.Radio, key, StateOwnership.StationGlobal, template, c, notes);

            void Telemetry(RigTarget target, string key, string notes)
                => Add(target, key, StateOwnership.Telemetry, null, Confidence.FromVendorSource, notes);

            // ================================================================ //
            // SLICE — client-owned, globally observable.
            // The whole receiver surface lives here.
            // ================================================================ //

            Slice("RF_frequency", "slice tune {i} {v}", Confidence.FromVendorSource,
                "Reported as RF_frequency in MHz. Written with 'slice tune', NOT 'slice set'. FlexLib additionally refuses to send any tune at all while the slice is locked, so through the app a locked slice swallows retunes client-side and the radio never hears them.");
            Slice("mode", "slice set {i} mode={v}", Confidence.FromVendorSource,
                "FlexLib dedups mode against its own cache before sending, so setting the mode to what it already believes sends nothing at all. Raw wire does not dedup, which is why the observer reads from the radio.", upper: true);
            Slice("filter_lo", null, Confidence.FromVendorSource,
                "Composite. Written together with filter_hi as 'filt <index> <low> <high>'. FlexLib drops the command entirely when low is not below high.");
            Slice("filter_hi", null, Confidence.FromVendorSource,
                "Composite with filter_lo. See CompositeCommand.");
            Slice("agc_mode", "slice set {i} agc_mode={v}", Confidence.FromVendorSource,
                "Legal values are off, slow, med, fast. Note 'med' and not 'medium' — the long spelling is silently not recognised.");
            Slice("agc_threshold", "slice set {i} agc_threshold={v}", Confidence.FromVendorSource, "0 to 100.");
            Slice("agc_off_level", "slice set {i} agc_off_level={v}", Confidence.FromVendorSource, "Only meaningful with AGC off.");
            Slice("nb", "slice set {i} nb={v}", Confidence.FromVendorSource, "Noise blanker on or off.");
            Slice("nb_level", "slice set {i} nb_level={v}", Confidence.FromVendorSource, "Noise blanker depth, 0 to 100.");
            Slice("wnb", "slice set {i} wnb={v}", Confidence.FromVendorSource, "Wideband noise blanker, a separate control from nb.");
            Slice("wnb_level", "slice set {i} wnb_level={v}", Confidence.FromVendorSource, "Wideband noise blanker depth.");
            Slice("nr", "slice set {i} nr={v}", Confidence.FromVendorSource, "Classic noise reduction.");
            Slice("nr_level", "slice set {i} nr_level={v}", Confidence.FromVendorSource, "Classic noise reduction depth.");
            Slice("anf", "slice set {i} anf={v}", Confidence.FromVendorSource, "Automatic notch filter.");
            Slice("anf_level", "slice set {i} anf_level={v}", Confidence.FromVendorSource, "ANF depth.");
            Slice("apf", "slice set {i} apf={v}", Confidence.FromVendorSource, "Audio peaking filter, CW. Distinct from the radio-level APF.");
            Slice("apf_level", "slice set {i} apf_level={v}", Confidence.FromVendorSource, "APF depth.");

            // The newer DSP family. Every one of these reports under a short
            // name and is written under a long one, and several have no status
            // key for their level at all.
            Slice("nrl", "slice set {i} lms_nr={v}", Confidence.FromVendorSource,
                "LMS noise reduction. Reports as 'nrl', writes as 'lms_nr'. Its LEVEL (lms_nr_level) is writable but has NO status key, so it can be set and never read back — an honest harness reports that level as unobservable rather than assuming the write took.");
            Slice("anfl", "slice set {i} lms_anf={v}", Confidence.FromVendorSource,
                "LMS automatic notch. Reports as 'anfl', writes as 'lms_anf'. Its level is likewise write-only.");
            Slice("nrs", "slice set {i} speex_nr={v}", Confidence.FromVendorSource,
                "Speex noise reduction. Reports as 'nrs', writes as 'speex_nr'. Level write-only.");
            Slice("rnn", "slice set {i} rnnoise={v}", Confidence.FromVendorSource,
                "RNNoise. Reports as 'rnn', writes as 'rnnoise'.");
            Slice("anft", "slice set {i} anft={v}", Confidence.FromVendorSource, "ANF-T. Same name both ways.");
            Slice("nrf", "slice set {i} nrf={v}", Confidence.FromVendorSource, "NRF. Level is write-only.");

            Slice("rit_on", "slice set {i} rit_on={v}", Confidence.FromVendorSource, "Receiver incremental tuning enable.");
            Slice("rit_freq", "slice set {i} rit_freq={v}", Confidence.FromVendorSource, "RIT offset in Hz, signed.");
            Slice("xit_on", "slice set {i} xit_on={v}", Confidence.FromVendorSource, "Transmitter incremental tuning enable.");
            Slice("xit_freq", "slice set {i} xit_freq={v}", Confidence.FromVendorSource, "XIT offset in Hz, signed.");

            Slice("tx", "slice set {i} tx={v}", Confidence.FromVendorSource,
                "Marks this slice as the transmit slice FOR ITS OWNING CLIENT. Every connected client has its own. This is the field the MultiFlex trap is really about: read from our own connection after creating our own slice, it would describe us and not the application.");
            Slice("active", "slice set {i} active={v}", Confidence.FromVendorSource, "The owning client's active slice.");
            Slice("rxant", "slice set {i} rxant={v}", Confidence.FromVendorSource, "Receive antenna. Legal names come from the 'ant list' reply command, not from a status key.");
            Slice("txant", "slice set {i} txant={v}", Confidence.FromVendorSource,
                "Transmit antenna. Note this is where the station's TX antenna actually lives — there is no tx_antenna key in transmit status.");
            Slice("audio_level", "slice set {i} audio_level={v}", Confidence.FromVendorSource,
                "Receive audio level. The wire key is audio_level even though the vendor property is called AudioGain.");
            Slice("audio_mute", "slice set {i} audio_mute={v}", Confidence.FromVendorSource, "Per-slice mute.");
            Slice("audio_pan", "slice set {i} audio_pan={v}", Confidence.FromVendorSource, "Per-slice stereo pan.");
            Slice("dax", "slice set {i} dax={v}", Confidence.FromVendorSource, "DAX receive channel bound to this slice.");
            Slice("step", "slice set {i} step={v}", Confidence.FromVendorSource, "Tuning step in Hz. FlexLib clamps below 1.");
            Slice("diversity", "slice set {i} diversity={v}", Confidence.FromVendorSource,
                "Two-SCU radios only, which the 8600 is. FlexLib refuses to send this at all when it believes diversity is not allowed.");
            Slice("loopa", "slice set {i} loopa={v}", Confidence.FromVendorSource, "Loop A antenna path.");
            Slice("loopb", "slice set {i} loopb={v}", Confidence.FromVendorSource, "Loop B antenna path.");
            Slice("squelch", "slice set {i} squelch={v}", Confidence.FromVendorSource, "Squelch enable.");
            Slice("squelch_level", "slice set {i} squelch_level={v}", Confidence.FromVendorSource, "Squelch threshold.");
            Slice("record", "slice set {i} record={v}", Confidence.FromVendorSource, "Slice recorder.");
            Slice("play", "slice set {i} play={v}", Confidence.FromVendorSource,
                "Slice playback. Its status value can be the literal string 'disabled' rather than 0 or 1, so a numeric parse of this field will fail on a perfectly normal radio.");
            Slice("lock", null, Confidence.FromVendorSource,
                "Frequency lock. Written as two different verbs, 'slice lock <n>' and 'slice unlock <n>', not as a value.",
                writer: (index, value) =>
                    (IsTruthy(value) ? "slice lock " : "slice unlock ")
                    + index.ToString(CultureInfo.InvariantCulture));
            Slice("rtty_mark", "slice set {i} rtty_mark={v}", Confidence.Assumed, "RTTY mark tone.");
            Slice("rtty_shift", "slice set {i} rtty_shift={v}", Confidence.Assumed, "RTTY shift.");
            Slice("digl_offset", "slice set {i} digl_offset={v}", Confidence.Assumed, "DIGL centre offset.");
            Slice("digu_offset", "slice set {i} digu_offset={v}", Confidence.Assumed, "DIGU centre offset.");
            Slice("tx_offset_freq", "slice set {i} tx_offset_freq={v}", Confidence.Assumed, "Transmit frequency offset.");
            Slice("fm_repeater_offset_freq", "slice set {i} fm_repeater_offset_freq={v}", Confidence.Assumed, "FM repeater offset.");
            Slice("repeater_offset_dir", "slice set {i} repeater_offset_dir={v}", Confidence.Assumed, "down, simplex or up.");
            Slice("fm_tone_mode", "slice set {i} fm_tone_mode={v}", Confidence.Assumed, "CTCSS tone mode.");
            Slice("fm_tone_value", "slice set {i} fm_tone_value={v}", Confidence.Assumed, "CTCSS tone.");
            Slice("fm_tone_burst", "slice set {i} fm_tone_burst={v}", Confidence.Assumed, "1750 Hz tone burst.");
            Slice("fm_deviation", "slice set {i} fm_deviation={v}", Confidence.Assumed, "FM deviation.");
            Slice("dfm_pre_de_emphasis", "slice set {i} dfm_pre_de_emphasis={v}", Confidence.Assumed, "FM pre/de-emphasis.");

            Add(RigTarget.Slice, "client_handle", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "The owner. This single field decides whether a slice is ours to touch, and it is what makes honest observation of the application's slices possible at all.");
            Add(RigTarget.Slice, "in_use", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "Whether the slice exists. Created with 'slice create', released with 'slice remove'. The radio announces a release as in_use=0 and sends no 'removed' token.");
            Add(RigTarget.Slice, "index_letter", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "The A/B/C letter the operator hears. Assigned by the radio, per client.");
            Add(RigTarget.Slice, "rfgain", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "Reported here but NOT settable here. The vendor's slice-level setter is marked obsolete and is also malformed — it emits 'slice set0 rfgain=...' with no space — so it could never have worked. RF gain is a panadapter property; write it there.");
            Add(RigTarget.Slice, "wide", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "Preselector bypass, reported by the radio. Read only.");
            Add(RigTarget.Slice, "qsk", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "Reported per slice, read only. The settable QSK lives on the CWX object.");
            Add(RigTarget.Slice, "owner", StateOwnership.ClientOwned, null, Confidence.FromVendorSource, "Slice owner index.");
            Add(RigTarget.Slice, "pan", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "The panadapter stream id this slice belongs to. Follow it to find the slice's RF gain, preamp and band.");
            Add(RigTarget.Slice, "mode_list", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Legal modes, comma separated. Read this instead of guessing.");
            Add(RigTarget.Slice, "ant_list", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Legal receive antennas for this slice.");
            Add(RigTarget.Slice, "tx_ant_list", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Legal transmit antennas for this slice.");
            Add(RigTarget.Slice, "step_list", StateOwnership.Telemetry, null, Confidence.FromVendorSource,
                "Legal tuning steps. The vendor's setter for this is malformed — 'slice set 0step_list=' with no space — and is not marked obsolete.");
            Add(RigTarget.Slice, "dax_clients", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "How many DAX clients are attached.");
            Add(RigTarget.Slice, "diversity_child", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Diversity pairing.");
            Add(RigTarget.Slice, "diversity_index", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Diversity pairing.");

            // ================================================================ //
            // PANADAPTER — client-owned, and the real home of RF gain and band.
            // ================================================================ //

            Pan("band", "display pan set {x} band={v}", Confidence.FromVendorSource,
                "BAND IS A PANADAPTER PROPERTY, not a slice one and not a radio one. There is no 'slice set N band='. A band change means writing this and letting the radio retune, or tuning the slice directly.");
            Pan("rfgain", "display pan set {x} rfgain={v}", Confidence.FromVendorSource,
                "RF gain, which on a Flex is the preamp and attenuator control rolled into one signed dB figure. It is per panadapter, which is to say per SCU, and NOT per slice.");
            Pan("rxant", "display pan set {x} rxant={v}", Confidence.FromVendorSource, "Which antenna feeds this SCU.");
            Pan("band_zoom", "display pan set {x} band_zoom={v}", Confidence.FromVendorSource, "Zoom to band.");
            Pan("segment_zoom", "display pan set {x} segment_zoom={v}", Confidence.FromVendorSource, "Zoom to segment.");
            Pan("center", "display pan set {x} center={v}", Confidence.Assumed, "Centre frequency in MHz.");
            Pan("bandwidth", "display pan set {x} bandwidth={v}", Confidence.Assumed, "Span in MHz.");
            Add(RigTarget.Display, "pre", StateOwnership.ClientOwned, null, Confidence.FromVendorSource,
                "Preamp, reported as an opaque STRING and never parsed by the vendor library. There is no command that writes it — the writable control is rfgain. THERE IS NO SEPARATE ATTENUATOR CONCEPT ANYWHERE IN THIS API.");
            Add(RigTarget.Display, "wide", StateOwnership.ClientOwned, null, Confidence.FromVendorSource, "Preselector bypass state for this SCU.");
            Add(RigTarget.Display, "ant_list", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Legal antennas for this SCU.");
            Add(RigTarget.Display, "xvtr", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Transverter attached to this panadapter, if any.");
            Add(RigTarget.Display, "client_handle", StateOwnership.ClientOwned, null, Confidence.FromVendorSource, "The owning client.");
            Add(RigTarget.Display, "display_kind", StateOwnership.Telemetry, null, Confidence.FromVendorSource, "Synthesised by this parser: 'pan' or 'waterfall'.");

            // ================================================================ //
            // TRANSMIT — station-global, and a vocabulary minefield.
            // ================================================================ //

            Tx("rfpower", "transmit set rfpower={v}", Confidence.FromVendorSource,
                "Transmit power, 0 to 100. The transmit harness's power ceiling is enforced against this. FlexLib clamps to 0-100 and then dedups, so a set to the value it already believes sends nothing.");
            Tx("tunepower", "transmit set tunepower={v}", Confidence.FromVendorSource,
                "Power used for the tune carrier and for ATU tuning. Distinct from rfpower and easy to leave high by accident.");
            Tx("max_power_level", "transmit set max_power_level={v}", Confidence.FromVendorSource, "Per-band maximum.");
            Tx("mic_level", "transmit set miclevel={v}", Confidence.FromVendorSource,
                "MIC GAIN. Reports as mic_level, writes as miclevel — no underscore. Sending 'transmit set mic_level=' does nothing useful.");
            Tx("mic_selection", "mic input {v}", Confidence.FromVendorSource,
                "Mic source. Written with the top-level 'mic' verb, not with 'transmit set'. Uppercased on the way out. Legal values come from the 'mic list' reply command.", upper: true);
            Tx("mic_boost", "mic boost {v}", Confidence.FromVendorSource, "20 dB mic preamp. Top-level 'mic' verb.");
            Tx("mic_bias", "mic bias {v}", Confidence.FromVendorSource, "Electret bias on the front jack. Top-level 'mic' verb.");
            Tx("mic_acc", "mic acc {v}", Confidence.FromVendorSource, "Accessory-jack mic enable. Top-level 'mic' verb.");
            Tx("am_carrier_level", "transmit set am_carrier={v}", Confidence.FromVendorSource,
                "Reports as am_carrier_level, writes as am_carrier.");
            Tx("compander", "transmit set compander={v}", Confidence.FromVendorSource, "Companding on or off.");
            Tx("compander_level", "transmit set compander_level={v}", Confidence.FromVendorSource, "Companding depth.");
            Tx("speech_processor_enable", "transmit set speech_processor_enable={v}", Confidence.FromVendorSource, "Speech processor.");
            Tx("speech_processor_level", "transmit set speech_processor_level={v}", Confidence.FromVendorSource, "Speech processor depth.");
            Tx("vox_enable", "transmit set vox_enable={v}", Confidence.FromVendorSource,
                "VOX. Station-global and it can KEY THE RADIO on its own, so the non-transmitting harness never touches it.");
            Tx("vox_level", "transmit set vox_level={v}", Confidence.FromVendorSource, "VOX threshold.");
            Tx("vox_delay", "transmit set vox_delay={v}", Confidence.FromVendorSource, "VOX hang time.");
            Tx("sb_monitor", "transmit set mon={v}", Confidence.FromVendorSource,
                "Transmit monitor. Reports as sb_monitor, writes as mon.");
            Tx("mon_gain_sb", "transmit set mon_gain_sb={v}", Confidence.FromVendorSource, "Sideband monitor level.");
            Tx("mon_gain_cw", "transmit set mon_gain_cw={v}", Confidence.FromVendorSource, "CW monitor level.");
            Tx("mon_pan_sb", "transmit set mon_pan_sb={v}", Confidence.FromVendorSource, "Sideband monitor pan.");
            Tx("mon_pan_cw", "transmit set mon_pan_cw={v}", Confidence.FromVendorSource, "CW monitor pan.");
            Tx("dax", "transmit set dax={v}", Confidence.FromVendorSource, "DAX transmit enable.");
            Tx("hwalc_enabled", "transmit set hwalc_enabled={v}", Confidence.FromVendorSource, "Hardware ALC from an amplifier.");
            Tx("inhibit", "transmit set inhibit={v}", Confidence.FromVendorSource, "Transmit inhibit. The key is 'inhibit', not 'tx_inhibit'.");
            Tx("met_in_rx", "transmit set met_in_rx={v}", Confidence.FromVendorSource, "Show transmit meters while receiving.");
            Tx("show_tx_in_waterfall", "transmit set show_tx_in_waterfall={v}", Confidence.FromVendorSource, "Draw own transmission in the waterfall.");
            Tx("lo", null, Confidence.FromVendorSource,
                "Transmit passband low edge. Composite: written together with 'hi' as 'transmit set filter_low=X filter_high=Y'. Note the transmit side spells these lo/hi while the slice side spells them filter_lo/filter_hi.");
            Tx("hi", null, Confidence.FromVendorSource, "Transmit passband high edge. Composite with 'lo'.");

            // CW settings report under transmit but are written with the
            // top-level 'cw' verb.
            Tx("pitch", "cw pitch {v}", Confidence.FromVendorSource, "CW sidetone and offset pitch in Hz. Written with the 'cw' verb.");
            Tx("speed", "cw wpm {v}", Confidence.FromVendorSource, "Keyer speed. Reports as 'speed', writes as 'cw wpm'.");
            Tx("sidetone", "cw sidetone {v}", Confidence.FromVendorSource, "Sidetone audible.");
            Tx("break_in", "cw break_in {v}", Confidence.FromVendorSource, "Break-in enable.");
            Tx("break_in_delay", "cw break_in_delay {v}", Confidence.FromVendorSource, "Break-in hang time.");
            Tx("iambic", "cw iambic {v}", Confidence.FromVendorSource, "Iambic keying enable.");
            Tx("iambic_mode", "cw mode {v}", Confidence.FromVendorSource, "Iambic A or B. Reports as iambic_mode, writes as 'cw mode'.");
            Tx("cwl_enabled", "cw cwl_enabled {v}", Confidence.FromVendorSource, "CW lower sideband.");
            Tx("swap_paddles", "cw swap {v}", Confidence.FromVendorSource, "Reports as swap_paddles, writes as 'cw swap'.");
            Tx("synccwx", "cw synccwx {v}", Confidence.FromVendorSource, "Synchronise CWX with the keyer.");

            Telemetry(RigTarget.Transmit, "tune",
                "Whether the tune carrier is running. NEVER restored: the write for it is 'transmit tune 1', which keys the radio. Also note the radio drops another client's tune state under MultiFlex.");
            Telemetry(RigTarget.Transmit, "tune_mode", "One-tone or two-tone tune. Values are single_tone and two_tone.");
            Telemetry(RigTarget.Transmit, "freq", "Current transmit frequency, derived from the transmit slice.");
            Telemetry(RigTarget.Transmit, "tx_slice_mode", "The transmit slice's mode, mirrored into the transmit object.");
            Telemetry(RigTarget.Transmit, "mon_available", "Whether monitoring is possible at all.");
            Telemetry(RigTarget.Transmit, "tx_filter_changes_allowed", "Whether the passband may be changed right now.");
            Telemetry(RigTarget.Transmit, "tx_rf_power_changes_allowed", "Whether power may be changed right now.");
            Telemetry(RigTarget.Transmit, "max_internal_pa_power", "Ceiling of the internal PA.");
            Telemetry(RigTarget.Transmit, "raw_iq_enable", "Raw IQ transmit path.");

            // ================================================================ //
            // INTERLOCK — the authoritative transmit state, plus wiring.
            // ================================================================ //

            Telemetry(RigTarget.Interlock, "state",
                "THE authoritative answer to 'are we transmitting'. Values: RECEIVE, READY, NOT_READY, PTT_REQUESTED, TRANSMITTING, TX_FAULT, TIMEOUT, STUCK_INPUT, UNKEY_REQUESTED. THERE IS NO MOX STATUS KEY ANYWHERE ON THE WIRE — the vendor library derives MOX from exactly this field, and so must any honest observer.");
            Telemetry(RigTarget.Interlock, "tx_allowed", "Whether the radio would permit a key-down right now.");
            Telemetry(RigTarget.Interlock, "reason",
                "Why transmit is blocked. Values include RCA_TXREQ, ACC_TXREQ, BAD_MODE, TUNED_TOO_FAR, OUT_OF_BAND, OUT_OF_PA_RANGE, CLIENT_TX_INHIBIT, XVTR_RX_ONLY, NO_TX_ASSIGNED.");
            Telemetry(RigTarget.Interlock, "source", "What is asking to transmit: SW, MIC, ACC, RCA or TUNE.");
            Telemetry(RigTarget.Interlock, "tx_client_handle",
                "WHICH CLIENT holds the transmitter. This is how the harness tells 'the application is transmitting' from 'somebody else is'. Always 0x-prefixed.");
            Telemetry(RigTarget.Interlock, "amplifier", "Comma-separated amplifier handles.");
            Add(RigTarget.Interlock, "timeout", StateOwnership.StationGlobal, "interlock timeout={v}", Confidence.FromVendorSource,
                "Transmit timeout in milliseconds. Note the command form is 'interlock <key>=<value>' — there is no 'interlock set'.");
            Add(RigTarget.Interlock, "rca_txreq_enable", StateOwnership.StationGlobal, "interlock rca_txreq_enable={v}", Confidence.FromVendorSource, "RCA transmit-request input.");
            Add(RigTarget.Interlock, "acc_txreq_enable", StateOwnership.StationGlobal, "interlock acc_txreq_enable={v}", Confidence.FromVendorSource, "Accessory transmit-request input.");
            Add(RigTarget.Interlock, "rca_txreq_polarity", StateOwnership.StationGlobal, "interlock rca_txreq_polarity={v}", Confidence.FromVendorSource, "RCA request polarity.");
            Add(RigTarget.Interlock, "acc_txreq_polarity", StateOwnership.StationGlobal, "interlock acc_txreq_polarity={v}", Confidence.FromVendorSource, "Accessory request polarity.");
            Add(RigTarget.Interlock, "tx_delay", StateOwnership.StationGlobal, "interlock tx_delay={v}", Confidence.FromVendorSource, "Transmit delay.");
            Add(RigTarget.Interlock, "tx1_enabled", StateOwnership.StationGlobal, "interlock tx1_enabled={v}", Confidence.FromVendorSource, "TX1 relay.");
            Add(RigTarget.Interlock, "tx2_enabled", StateOwnership.StationGlobal, "interlock tx2_enabled={v}", Confidence.FromVendorSource, "TX2 relay.");
            Add(RigTarget.Interlock, "tx3_enabled", StateOwnership.StationGlobal, "interlock tx3_enabled={v}", Confidence.FromVendorSource, "TX3 relay.");
            Add(RigTarget.Interlock, "acc_tx_enabled", StateOwnership.StationGlobal, "interlock acc_tx_enabled={v}", Confidence.FromVendorSource, "Accessory TX relay.");
            Add(RigTarget.Interlock, "tx1_delay", StateOwnership.StationGlobal, "interlock tx1_delay={v}", Confidence.FromVendorSource, "TX1 relay delay.");
            Add(RigTarget.Interlock, "tx2_delay", StateOwnership.StationGlobal, "interlock tx2_delay={v}", Confidence.FromVendorSource, "TX2 relay delay.");
            Add(RigTarget.Interlock, "tx3_delay", StateOwnership.StationGlobal, "interlock tx3_delay={v}", Confidence.FromVendorSource, "TX3 relay delay.");
            Add(RigTarget.Interlock, "acc_tx_delay", StateOwnership.StationGlobal, "interlock acc_tx_delay={v}", Confidence.FromVendorSource, "Accessory relay delay.");

            // ================================================================ //
            // ATU
            // ================================================================ //

            Telemetry(RigTarget.Atu, "status",
                "The key is 'status', not 'atu_status'. Values: NONE, TUNE_NOT_STARTED, TUNE_IN_PROGRESS, TUNE_BYPASS, TUNE_SUCCESSFUL, TUNE_OK, TUNE_FAIL_BYPASS, TUNE_FAIL, TUNE_ABORTED, TUNE_MANUAL_BYPASS. Note the failure spellings are TUNE_FAIL and TUNE_FAIL_BYPASS — there is no TUNE_FAILED and no TUNE_TIMEOUT.");
            Telemetry(RigTarget.Atu, "atu_enabled", "Whether the tuner is in circuit. No write path.");
            Telemetry(RigTarget.Atu, "using_mem", "Whether the present solution came from memory.");
            Add(RigTarget.Atu, "memories_enabled", StateOwnership.StationGlobal, "atu set memories_enabled={v}", Confidence.FromVendorSource,
                "Whether the tuner reuses stored solutions. The only 'atu set' form there is, and it does NOT transmit, so it is safe to exercise. Note the vendor library silently forces this to 0 when an external tuner is present.");

            // ================================================================ //
            // RADIO — station-global
            // ================================================================ //

            Radio("nickname", "radio name {v}", Confidence.FromVendorSource,
                "The radio's name. Reports as 'nickname', writes as 'radio name'. Harmless and reversible, which makes it the safest possible first test of the restore path.");
            Radio("callsign", "radio callsign {v}", Confidence.FromVendorSource, "Station callsign held in the radio.");
            Radio("backlight", "radio backlight {v}", Confidence.FromVendorSource, "Front panel backlight.");
            Radio("screensaver", "radio screensaver {v}", Confidence.FromVendorSource, "Front panel screensaver: model, name or callsign.");
            Radio("binaural_rx", "radio set binaural_rx={v}", Confidence.FromVendorSource, "Binaural receive.");
            Radio("mute_local_audio_when_remote", "radio set mute_local_audio_when_remote={v}", Confidence.FromVendorSource, "Mute the radio's own speaker during remote operation.");
            Radio("tnf_enabled", "radio set tnf_enabled={v}", Confidence.FromVendorSource, "Tracking notch filters globally enabled.");
            Radio("full_duplex_enabled", "radio set full_duplex_enabled={v}", Confidence.FromVendorSource, "Full duplex. Two-SCU radios only.");
            Radio("remote_on_enabled", "radio set remote_on_enabled={v}", Confidence.FromVendorSource, "Wake-on-LAN style remote power on.");
            Radio("rtty_mark_default", "radio set rtty_mark_default={v}", Confidence.FromVendorSource, "Default RTTY mark tone.");
            Radio("low_latency_digital_modes", "radio set low_latency_digital_modes={v}", Confidence.FromVendorSource, "Low latency digital mode path.");
            Radio("freq_error_ppb", "radio set freq_error_ppb={v}", Confidence.FromVendorSource, "Reference frequency correction.");
            Radio("cal_freq", "radio set cal_freq={v}", Confidence.FromVendorSource, "Calibration frequency.");
            Radio("enforce_private_ip_connections", "radio set enforce_private_ip_connections={v}", Confidence.FromVendorSource, "Refuse connections from outside the local network.");
            Radio("mf_enable", "radio set mf_enable={v}", Confidence.FromVendorSource, "MultiFlex enable.");

            Add(RigTarget.Radio, "lineout_gain", StateOwnership.StationGlobal, null, Confidence.Assumed,
                "Line output level. Reported, but the vendored library has no command that writes it, so the harness will not attempt one rather than guess a spelling.");
            Add(RigTarget.Radio, "lineout_mute", StateOwnership.StationGlobal, null, Confidence.Assumed, "Line output mute. Same reasoning as lineout_gain.");
            Add(RigTarget.Radio, "headphone_gain", StateOwnership.StationGlobal, null, Confidence.Assumed, "Headphone level. Same reasoning.");
            Add(RigTarget.Radio, "headphone_mute", StateOwnership.StationGlobal, null, Confidence.Assumed, "Headphone mute. Same reasoning.");
            Add(RigTarget.Radio, "front_speaker_mute", StateOwnership.StationGlobal, null, Confidence.Assumed, "Front speaker mute. Same reasoning.");

            Telemetry(RigTarget.Radio, "model", "Radio model.");
            Telemetry(RigTarget.Radio, "slices", "How many slices the radio supports.");
            Telemetry(RigTarget.Radio, "panadapters", "How many panadapters the radio supports.");
            Telemetry(RigTarget.Radio, "daxiq_available", "DAX IQ capacity remaining.");
            Telemetry(RigTarget.Radio, "daxiq_capacity", "Total DAX IQ capacity.");
            Telemetry(RigTarget.Radio, "external_pa_allowed", "Whether an external amplifier is permitted.");
            Telemetry(RigTarget.Radio, "pll_done", "Reference PLL calibration state.");
            Telemetry(RigTarget.Radio, "alpha", "Alpha firmware flag.");
            Telemetry(RigTarget.Radio, "importing", "A profile import is in progress.");
            Telemetry(RigTarget.Radio, "auto_save", "Automatic profile save.");
            Telemetry(RigTarget.Radio, "unity_tests_complete", "Factory self-test flag.");

            // ================================================================ //
            // CLIENT — identity, all read-only from an observer's point of view
            // ================================================================ //

            Telemetry(RigTarget.Client, "program",
                "The connecting application's name. This is how the harness tells JJ Flexible's client apart from its own and from any other operator.");
            Telemetry(RigTarget.Client, "station", "The operator's station name. Spaces arrive encoded as U+007F.");
            Telemetry(RigTarget.Client, "client_id", "Stable per-installation identity.");
            Telemetry(RigTarget.Client, "local_ptt", "Whether that client holds local PTT.");
            Telemetry(RigTarget.Client, "connected", "Synthesised by this parser from the bare connected/disconnected token.");

            // ================================================================ //
            // METER — descriptors only. Values arrive over UDP.
            // ================================================================ //

            Telemetry(RigTarget.Meter, "src", "SLC for a slice meter, AMP for an amplifier meter, HAAPI otherwise.");
            Telemetry(RigTarget.Meter, "num", "Index within the source.");
            Telemetry(RigTarget.Meter, "nam", "The meter's name — FWDPWR, SWR, MIC, COMPPEAK, PATEMP, LEVEL. Note the key is 'nam', not 'name'.");
            Telemetry(RigTarget.Meter, "low", "Bottom of range.");
            Telemetry(RigTarget.Meter, "hi", "Top of range. The key is 'hi', not 'high'.");
            Telemetry(RigTarget.Meter, "unit", "Volts, Amps, dB, dBm, dBFS, degF, degC, SWR, Watts or Percent.");
            Telemetry(RigTarget.Meter, "desc", "Human description.");

            return list.ToArray();
        }

        private static bool IsTruthy(string value)
            => string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n != 0);

        private sealed class TargetKeyComparer : IEqualityComparer<(RigTarget, string)>
        {
            public static readonly TargetKeyComparer Instance = new();

            public bool Equals((RigTarget, string) x, (RigTarget, string) y)
                => x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

            public int GetHashCode((RigTarget, string) obj)
                => HashCode.Combine(obj.Item1, StringComparer.Ordinal.GetHashCode(obj.Item2));
        }
    }
}
