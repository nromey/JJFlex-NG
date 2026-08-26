using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Radios.Fixer.Evidence
{
    /// <summary>One setting the fingerprint can read: a stable key, the name
    /// an operator knows it by, and a guarded read of its current value as
    /// display text. Empty means "cannot be read right now" — an honest state
    /// the staleness check names rather than treating as a change.</summary>
    public sealed class FixerSettingProbe
    {
        public string Key { get; }
        public string Name { get; }
        private readonly Func<string> _read;

        public FixerSettingProbe(string key, string name, Func<string> read)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("a probe needs a key", nameof(key));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a probe needs the operator's name for the setting", nameof(name));
            Key = key;
            Name = name;
            _read = read ?? throw new ArgumentNullException(nameof(read));
        }

        /// <summary>The current value, or empty when it cannot be read. Never
        /// throws — an unreadable setting is a fact to record, not a failure
        /// to propagate into a diagnostic.</summary>
        public string Read()
        {
            try { return (_read() ?? "").Trim(); }
            catch { return ""; }
        }
    }

    /// <summary>
    /// The declared settings dependencies of one stage set: which settings
    /// each stage depends on, and how to read them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A declared list per stage, deliberately NOT snapshot-everything</b>
    /// (#252's design note). Snapshotting everything makes every stage stale
    /// on every unrelated change and trains the operator to ignore the
    /// warning — which costs more than the warning is worth. When membership
    /// is in doubt, leave the setting out: a missed dependency understates
    /// staleness once; a spurious one cries wolf forever.
    /// </para>
    /// <para>
    /// The natural long-term home for the declarations is on
    /// <c>FixerStage</c> itself, beside the stage's other data. That file is
    /// being restructured by another track this sprint, so the declarations
    /// live here, keyed by stage id, and the move is reported rather than
    /// made.
    /// </para>
    /// </remarks>
    public sealed class FixerSettingProbeSet
    {
        private readonly Dictionary<string, FixerSettingProbe> _probes;
        private readonly Dictionary<string, IReadOnlyList<string>> _declaredByStage;

        public FixerSettingProbeSet(IEnumerable<FixerSettingProbe> probes,
                                    IReadOnlyDictionary<string, IReadOnlyList<string>> declaredByStage)
        {
            if (probes == null) throw new ArgumentNullException(nameof(probes));
            if (declaredByStage == null) throw new ArgumentNullException(nameof(declaredByStage));

            _probes = new Dictionary<string, FixerSettingProbe>(StringComparer.OrdinalIgnoreCase);
            foreach (FixerSettingProbe p in probes)
            {
                if (_probes.ContainsKey(p.Key))
                    throw new ArgumentException("duplicate probe key: " + p.Key, nameof(probes));
                _probes[p.Key] = p;
            }

            _declaredByStage = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in declaredByStage)
            {
                // A declared dependency nobody can read is a wiring bug, and it
                // must fail at construction — in the host's hands — rather than
                // silently producing fingerprints with holes in them.
                foreach (string key in kv.Value)
                    if (!_probes.ContainsKey(key))
                        throw new ArgumentException("stage '" + kv.Key + "' declares '" + key
                            + "' but no probe reads it", nameof(declaredByStage));
                _declaredByStage[kv.Key] = kv.Value;
            }
        }

        /// <summary>The keys a stage declared, or empty for a stage that
        /// declared nothing (which is a valid declaration: it means no setting
        /// change can stale it).</summary>
        public IReadOnlyList<string> DeclaredFor(string stageId)
            => _declaredByStage.TryGetValue(stageId ?? "", out IReadOnlyList<string> keys)
               ? keys : Array.Empty<string>();

        /// <summary>Read the declared settings for a stage, now. Called the
        /// moment a stage result is recorded, so the values are the ones the
        /// stage actually ran under.</summary>
        public IReadOnlyList<RecordedSetting> CaptureFor(string stageId)
        {
            var captured = new List<RecordedSetting>();
            foreach (string key in DeclaredFor(stageId))
            {
                FixerSettingProbe probe = _probes[key];
                captured.Add(new RecordedSetting
                {
                    Key = probe.Key,
                    Name = probe.Name,
                    Value = probe.Read(),
                });
            }
            return captured;
        }

        /// <summary>The current value of one setting, by key, or null for a
        /// key no probe reads.</summary>
        public RecordedSetting ReadCurrent(string key)
        {
            if (!_probes.TryGetValue(key ?? "", out FixerSettingProbe probe)) return null;
            return new RecordedSetting { Key = probe.Key, Name = probe.Name, Value = probe.Read() };
        }
    }

    /// <summary>
    /// The transmit stage set's declared dependencies and the probes that read
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership rationale, stage by stage (under-declared on purpose — see
    /// <see cref="FixerSettingProbeSet"/>):
    /// </para>
    /// <para>
    /// Stage 0 (audio-setup) largely IS the settings: the input device and
    /// host API it read, plus the PC-audio switch and the microphone profile
    /// state its findings turn on. Stage 1 (microphone-check) measures
    /// through the configured input device on its host API and nothing else.
    /// Stage 2 (transmitter-check) keys a tune carrier: tune power, the
    /// transmit antenna, frequency and mode are what the reading was taken
    /// under. Stages 3 and 4 transmit audio: antenna, frequency, mode, RF
    /// power and microphone gain shape what the SC_MIC meter and the power
    /// readings mean; stage 4 adds the input device and host API because the
    /// microphone is back in the path — that one difference is the point of
    /// the pair.
    /// </para>
    /// </remarks>
    public static class TransmitSettingProbes
    {
        public const string InputDevice = "input-device";
        public const string HostApi = "host-api";
        public const string PcAudio = "pc-audio";
        public const string MicProfile = "mic-profile";
        public const string TunePower = "tune-power";
        public const string RfPower = "rf-power";
        public const string TxAntenna = "tx-antenna";
        public const string Frequency = "frequency";
        public const string Mode = "mode";
        public const string MicGain = "mic-gain";

        /// <summary>
        /// Build the transmit set's probe set. <paramref name="radio"/> may
        /// return null (no radio connected — radio-side values read as
        /// unreadable); <paramref name="audioSetup"/> is the same reader the
        /// audio-setup stage uses, so the fingerprint and the stage cannot
        /// disagree about what the configuration says.
        /// </summary>
        public static FixerSettingProbeSet Build(Func<FlexBase> radio,
                                                 Func<AudioSetupFacts> audioSetup)
        {
            string FromRig(Func<FlexBase, string> read)
            {
                FlexBase rig;
                try { rig = radio?.Invoke(); } catch { rig = null; }
                if (rig == null) return "";
                try { return read(rig) ?? ""; } catch { return ""; }
            }

            AudioSetupFacts Audio()
            {
                try { return audioSetup?.Invoke(); } catch { return null; }
            }

            var probes = new[]
            {
                new FixerSettingProbe(InputDevice, "Input device", () =>
                {
                    AudioSetupFacts a = Audio();
                    if (a == null) return "";
                    return a.ConfiguredInputDevice.Length > 0
                        ? a.ConfiguredInputDevice : a.OpenInputDevice;
                }),
                new FixerSettingProbe(HostApi, "Audio host API", () =>
                {
                    AudioSetupFacts a = Audio();
                    if (a == null) return "";
                    return a.ConfiguredHostApi.Length > 0
                        ? a.ConfiguredHostApi : a.OpenHostApi;
                }),
                new FixerSettingProbe(PcAudio, "PC audio", () =>
                    FromRig(r => r.PCAudio ? "on" : "off")),
                new FixerSettingProbe(MicProfile, "Microphone profile", () =>
                    FromRig(r => r.MicProfileSelectionEmpty ? "empty" : "has settings")),
                new FixerSettingProbe(TunePower, "Tune power", () =>
                    FromRig(r => r.TunePower.ToString(CultureInfo.InvariantCulture) + " watts")),
                new FixerSettingProbe(RfPower, "RF power", () =>
                    FromRig(r => r.XmitPower.ToString(CultureInfo.InvariantCulture) + " watts")),
                new FixerSettingProbe(TxAntenna, "Transmit antenna", () =>
                    FromRig(r => r.TXAntennaName)),
                new FixerSettingProbe(Frequency, "Frequency", () =>
                    FromRig(r => FormatMHz(r.TXFrequency))),
                new FixerSettingProbe(Mode, "Mode", () =>
                    FromRig(r => r.Mode)),
                new FixerSettingProbe(MicGain, "Microphone gain", () =>
                    FromRig(r => r.MicGain.ToString(CultureInfo.InvariantCulture))),
            };

            var declared = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [TransmitStageSet.AudioSetup] =
                    new[] { InputDevice, HostApi, PcAudio, MicProfile },
                [TransmitStageSet.MicrophoneCheck] =
                    new[] { InputDevice, HostApi },
                [TransmitStageSet.TransmitterCheck] =
                    new[] { TunePower, TxAntenna, Frequency, Mode },
                [TransmitStageSet.InjectedTransmit] =
                    new[] { TxAntenna, Frequency, Mode, RfPower, MicGain },
                [TransmitStageSet.SpokenTransmit] =
                    new[] { InputDevice, HostApi, TxAntenna, Frequency, Mode, RfPower, MicGain },
            };

            return new FixerSettingProbeSet(probes, declared);
        }

        /// <summary>Hz to "14.203 MHz". Zero — a frequency nothing has set —
        /// reads as unreadable rather than as "0 MHz".</summary>
        internal static string FormatMHz(ulong hz)
        {
            if (hz == 0) return "";
            return (hz / 1_000_000.0).ToString("0.000###", CultureInfo.InvariantCulture) + " MHz";
        }
    }
}
