using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
// Deliberately no System.Windows.Forms here: this class must be callable from
// the background audio thread, and the old MessageBox error paths were exactly
// the thing that made that unsafe.
using System.Xml.Serialization;
using JJTrace;
using PortAudioSharp;

namespace JJPortaudio
{
    /// <summary>
    /// PC-side audio device selection and persistence for the PC-audio path
    /// (radio RX audio out to the computer, computer mic in to the radio).
    ///
    /// QB Track B, 2026-08-07: this class used to depend on the internal
    /// <c>devList</c> WinForms form for BOTH enumeration and selection UI.
    /// The form is gone; enumeration now lives here as a UI-free static API
    /// (<see cref="Enumerate"/>) and selection is done by the WPF Audio
    /// Devices dialog, which calls <see cref="SetConfiguredDevice"/>.
    ///
    /// Nothing in here shows UI or writes to the console. Failures come back
    /// as a status plus a human-readable message so the caller — which knows
    /// whether it is on the UI thread and which screen-reader verbosity
    /// applies — decides how to say it. The old code raised MessageBoxes from
    /// a background audio thread, where NVDA focus handoff was unreliable.
    ///
    /// The persisted schema (<see cref="cfg"/> / <see cref="Device"/>) is
    /// unchanged apart from two ADDED optional fields (hostApiTypeId,
    /// hostApiName) that make the saved identity survive device-list
    /// reshuffles. Old audioDevices.xml files load fine; the added fields
    /// simply come back as defaults until the next save.
    /// </summary>
    public class Devices
    {
        /// <summary>
        /// Device type, input or output.
        /// </summary>
        public enum DeviceTypes
        {
            none = 0,
            input,
            output
        }

        /// <summary>
        /// Why an enumeration attempt did not produce a usable device list.
        /// </summary>
        public enum EnumerationStatus
        {
            /// <summary>At least one usable device was found.</summary>
            Ok = 0,
            /// <summary>PortAudio itself would not start.</summary>
            InitFailed,
            /// <summary>PortAudio started but reported a device-count error.</summary>
            QueryFailed,
            /// <summary>PortAudio started and found no audio devices at all.</summary>
            NoDevices
        }

        /// <summary>
        /// Audio device abstraction. This is the serialized shape of
        /// audioDevices.xml — field names are part of the file format.
        /// </summary>
        public class Device
        {
            public int DevinfoID; // infoList ID. Advisory only: re-resolved every run.
            public DeviceTypes Type;
            public string Name;
            public int hostApi;
            public int maxInputChannels;
            public int maxOutputChannels;
            public double defaultLowInputLatency;
            public double defaultLowOutputLatency;
            public double defaultHighInputLatency;
            public double defaultHighOutputLatency;
            public double defaultSampleRate;

            // --- Added 2026-08-07 (QB Track B) ---------------------------------
            // PortAudio's host-API INDEX (hostApi, above) shifts when a host API
            // appears or disappears, so it cannot identify a device on its own.
            // The type id is a fixed enum value (MME, DirectSound, WASAPI, ...)
            // and the name is the human string for it; together with Name they
            // give a saved selection an identity that survives a re-plug into a
            // different USB port, a new interface arriving, or a device being
            // removed. Absent in files written before this date — matching falls
            // back to the old name+channels rule in that case.
            public int hostApiTypeId = -1;
            public string hostApiName;

            [XmlIgnore]
            public string ConfigFile;
        }

        // Configured devices, 1 input, 1 output.
        public class cfg
        {
            public Device[] devs = new Device[2];

            /// <summary>
            /// The host API the operator chose, as a stable PaHostApiTypeId.
            /// -1 means "never chosen", which resolves to
            /// <see cref="DefaultHostApiTypeId"/> against whatever the machine
            /// actually offers.
            /// </summary>
            /// <remarks>
            /// Added 2026-08-16 (Track E). Machine scope, alongside the two
            /// device choices, for the same reason they live here: a sound
            /// card and the driver model it is reached through both belong to
            /// the computer, not to a radio or an operator. Files written
            /// before this date deserialize with -1 and get the default.
            /// </remarks>
            public int selectedHostApiTypeId = -1;
        }
        private cfg configured = new cfg();

        /// <summary>
        /// A device as the system currently reports it. Not persisted — this is
        /// the live enumeration result the picker shows.
        /// </summary>
        public class DeviceInfo
        {
            public PortAudio.PaDeviceInfo Info;
            public DeviceTypes Type;
            public bool IsDefault;
            public int DeviceID;            // index into the system's device array
            public int HostApiTypeId = -1;  // stable PaHostApiTypeId value
            public string HostApiName = "";

            // --- Added 2026-08-12 (Mic Track), device identity index ----------
            // One physical device shows up once per host API — a USB interface
            // typically appears three times (MME, DirectSound, WASAPI) plus its
            // kernel pins. These fields record WHICH endpoints are the same
            // piece of hardware.
            //
            // Track E, 2026-08-16: this used to be a picker FOLDING rule — one
            // row per physical device, the rest hidden behind it — and folding
            // is what silently chose a host API on the operator's behalf,
            // landing on MME. MME resamples transparently, so it reports a tidy
            // 48 kHz whatever the hardware is really doing, and every rate
            // problem in the app was invisible behind it. The picker now
            // filters by a host API the operator SELECTS
            // (<see cref="SelectedHostApiTypeId"/>), which leaves no duplicates
            // to fold.
            //
            // The index itself stays, because two things still need it: the
            // Windows level control matches a PortAudio row to a Core Audio
            // endpoint by trying every name the hardware goes by (WASAPI's full
            // name, MME's truncated one), and the "this is your Windows
            // default" flag belongs to the hardware rather than to whichever
            // endpoint PortAudio happened to tag.

            /// <summary>
            /// The endpoint chosen to stand for this physical device when a
            /// caller needs one canonical row for it. Every member of a group
            /// points at the same representative, and the representative points
            /// at itself. Never null after <see cref="Enumerate"/>.
            /// </summary>
            /// <remarks>
            /// No longer a picker-visibility rule — every endpoint of the
            /// selected host API is its own row. This is identity only.
            /// </remarks>
            public DeviceInfo GroupOwner;

            /// <summary>
            /// The other endpoints for this same physical device, under other
            /// host APIs. Empty on non-representative rows.
            /// </summary>
            public IReadOnlyList<DeviceInfo> Alternates = new List<DeviceInfo>();

            /// <summary>True when this row is the one the picker shows for its group.</summary>
            public bool IsGroupOwner => ReferenceEquals(GroupOwner, this);

            /// <summary>
            /// Set on a synthetic row that stands in for a saved device the
            /// system no longer reports. It is not openable and must never
            /// reach the audio engine; it exists so an unplugged interface says
            /// so in the list instead of silently disappearing from it.
            /// </summary>
            public bool IsMissingSaved;

            /// <summary>The saved entry a missing row stands for. Null otherwise.</summary>
            public Device SavedDevice;

            /// <summary>
            /// Set on EVERY endpoint of a physical device when any one of them
            /// is the Windows default. Kept separate from
            /// <see cref="IsDefault"/>, which stays true of exactly the one
            /// endpoint PortAudio flagged: PortAudio's default input on the
            /// development machine was the MME endpoint, so a list filtered to
            /// WASAPI would otherwise stop calling the Windows default out by
            /// name — and the advanced list, which shows every endpoint at
            /// once, would claim several system defaults if this flag were the
            /// one it read.
            /// </summary>
            /// <remarks>
            /// Set on every member since 2026-08-16 (Track E). It used to be
            /// set only on a group's representative, which was correct while
            /// the representative was the only row shown; now any endpoint can
            /// be the row on screen, and the fact is about the hardware.
            /// </remarks>
            public bool GroupIsSystemDefault;

            public string Name => IsMissingSaved ? (SavedDevice?.Name ?? "") : (Info.name ?? "");
            public bool CanInput => Info.maxInputChannels > 0;
            public bool CanOutput => Info.maxOutputChannels > 0;

            /// <summary>
            /// Channels this device offers in its own direction (a DeviceInfo
            /// is created per direction, so an input row reports input
            /// channels and an output row output channels).
            /// </summary>
            public int NativeChannels => (Type == DeviceTypes.input)
                ? Info.maxInputChannels : Info.maxOutputChannels;

            /// <summary>
            /// How many channels the engine will actually open on this device:
            /// two whenever the hardware has two, one when it is genuinely
            /// mono. PortAudio's contract allows opening any channel count from
            /// 1 up to the device's maximum, so a four-channel interface opens
            /// as stereo and a one-channel headset mic opens as mono.
            /// </summary>
            public int OpenChannels => (NativeChannels >= StreamChannels) ? StreamChannels : 1;

            /// <summary>True when this device offers exactly one channel.</summary>
            public bool IsMono => NativeChannels == 1;

            /// <summary>
            /// True when the radio-audio engine can open this device. Every
            /// device with a channel in its direction qualifies.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Until 2026-08-16 this read <c>NativeChannels &gt;= StreamChannels</c>
            /// — the engine opened two channels unconditionally and could not
            /// upmix, so a mono device was listed and tagged unusable and its
            /// selection was refused. That refusal had no workaround for the
            /// person it hurt most: ganging two inputs and panning both to
            /// centre requires owning a multi-channel interface, and a single
            /// mono USB headset mic simply could not be used by the app at
            /// all. Mono devices are frequently somebody's only microphone.
            /// </para>
            /// <para>
            /// The engine now opens at <see cref="OpenChannels"/> and
            /// duplicates mono to stereo in the callback — the same expansion
            /// <see cref="MicProbe"/> has always done to feed its loudness
            /// meter — so this is capability again rather than a filter.
            /// </para>
            /// </remarks>
            public bool UsableForRadioAudio => NativeChannels >= 1;

            /// <summary>
            /// True when the rate Windows is running this device at is one
            /// Opus can encode, so it can carry transmit audio to the radio.
            /// </summary>
            /// <remarks>
            /// Only worth reading under an honest host API. MME reports 48 kHz
            /// for everything because it resamples on the way through, so this
            /// is always true there and says nothing; WASAPI and WDM-KS report
            /// the endpoint's real shared-mode format, which is the number
            /// that decides whether transmit works.
            /// </remarks>
            public bool RateCanCarryOpus =>
                AudioAnchor.isOpusRate((uint)Info.defaultSampleRate);

            /// <summary>
            /// How this device is attached, when Windows tells us plainly
            /// enough to be worth repeating. Empty when it does not.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Mic Track, 2026-08-12. Deliberately conservative: a confidently
            /// wrong label ("Built-in" on someone's USB interface) is worse
            /// than no label at all, because it is the kind of thing an
            /// operator will believe over their own hands.
            /// </para>
            /// <para>
            /// <b>Built-in versus a jack is NOT claimed, and that is a
            /// finding, not an omission.</b> The obvious richer source is the
            /// Windows Core Audio endpoint property store, and it was tried:
            /// on the development machine every endpoint — a USB audio
            /// interface and a set of virtual cables alike — reported form
            /// factor LineLevel, and the device instance path came back empty
            /// through the managed property store. A property that answers the
            /// same for everything answers nothing, so we do not build a claim
            /// on it. What is left is the device NAME, which is Windows' own
            /// words for the hardware, and the two or three things it says
            /// unambiguously.
            /// </para>
            /// </remarks>
            public string Connection => ClassifyConnection(Name, HostApiTypeId, Type);

            /// <summary>
            /// What a screen reader should read for this row. The device NAME
            /// comes first (2026-08-13) and every qualifier follows it. Both
            /// ways a person moves through this list — arrowing and
            /// type-ahead — charge by the word at the FRONT of the row:
            /// leading with "System default: " spent the opening words of the
            /// row most operators want on boilerplate, and WPF's first-letter
            /// navigation, which matches row text from its first character,
            /// could never jump to that device by name. The Windows default is
            /// still called out in words rather than by position, because
            /// "first in the list" is not information you can hear — it just
            /// speaks after the name now. A mono device still says so — the
            /// row itself carries the reason it cannot be chosen, instead of
            /// a silent refusal later.
            ///
            /// Names are passed through untouched. Windows device names have
            /// no stable format across vendors and drivers, and a transform
            /// that guesses wrong mangles the one thing the operator actually
            /// recognises — stripping a leading "Microphone" from "Microphone
            /// Array" would delete the device's entire identity.
            ///
            /// The one row that does NOT lead with its name: a saved device
            /// that has been unplugged leads with the warning, because for
            /// the person it applies to that is the single most important
            /// thing about the row.
            /// </summary>
            /// <remarks>
            /// The host API is named only where it is still a distinguishing
            /// fact. The basic list holds one host API at a time — the one the
            /// operator selected — so repeating "Windows WASAPI" after every
            /// single device is noise on every arrow press. It is spoken in the
            /// advanced view, where telling endpoints apart is the entire
            /// point, and on the one row in a basic list that can come from
            /// somewhere else: a saved device whose host API is no longer the
            /// selected one, kept on screen so a working configuration stays
            /// visible. That row must say where it comes from or the list is
            /// quietly showing two different things under one heading.
            /// </remarks>
            public string Display
            {
                get
                {
                    if (IsMissingSaved)
                        return "Not connected: " + Name + " — your saved choice, plug it back in or pick another";

                    // Basic view: the default belongs to the physical device,
                    // whichever endpoint of it is on screen. Advanced view: to
                    // the exact endpoint PortAudio flagged, because telling
                    // endpoints apart is the whole point of that view.
                    bool speaksAsDefault = ShowAdvancedDevices
                        ? IsDefault
                        : (IsDefault || GroupIsSystemDefault);

                    var sb = new StringBuilder();
                    sb.Append(Name);
                    // "system default" is the first qualifier because it is
                    // the decision-relevant one — the answer to "which do I
                    // pick if I don't care" — where the connection is merely
                    // descriptive.
                    if (speaksAsDefault) sb.Append(", system default");

                    string connection = Connection;
                    if (connection.Length > 0) sb.Append(", ").Append(connection);

                    bool apiIsWorthSaying = ShowAdvancedDevices
                        || (SelectedHostApiTypeId >= 0 && HostApiTypeId != SelectedHostApiTypeId);
                    if (apiIsWorthSaying && !string.IsNullOrEmpty(HostApiName))
                        sb.Append(" (").Append(HostApiName).Append(')');

                    string channels = DescribeChannels(this);
                    if (channels.Length > 0) sb.Append(", ").Append(channels);

                    string rate = DescribeRate(this);
                    if (rate.Length > 0) sb.Append(", ").Append(rate);

                    return sb.ToString();
                }
            }
        }

        /// <summary>
        /// What this device's channel count means for radio audio, in one
        /// vocabulary. Empty for plain stereo, which needs no words.
        /// </summary>
        /// <remarks>
        /// Track E, 2026-08-16. There used to be two ways to say the same
        /// thing about a mono device, in two vocabularies, neither giving a
        /// reason: the row tag appended " — mono, not usable yet" and
        /// selection-time spoke a separate "it needs a stereo device". An
        /// operator heard the row without the word "mono" in it at all, then
        /// hit a refusal that used a word the row never said. Both now come
        /// through here, and mono is no longer a refusal — it is a fact about
        /// what will happen to the audio.
        /// </remarks>
        public static string DescribeChannels(DeviceInfo d)
        {
            if (d == null || d.IsMissingSaved) return "";
            int n = d.NativeChannels;
            if (n <= 0) return "";
            if (n == 1)
            {
                return (d.Type == DeviceTypes.input)
                    ? "mono — sent to the radio on both channels"
                    : "mono — the two channels are mixed together for it";
            }
            if (n > StreamChannels) return n + " channels, used in stereo";
            return "";
        }

        /// <summary>
        /// A warning when the rate Windows runs this device at cannot carry
        /// transmit audio, or empty — which is the normal case and the point.
        /// </summary>
        /// <remarks>
        /// Deliberately silent under MME and DirectSound. Those backends
        /// resample on the way through, so the rate they report is not a fact
        /// about the hardware and a warning built on it would be a false
        /// alarm. WASAPI and WDM-KS report the endpoint's real shared-mode
        /// format, which is exactly the number that decides whether Opus can
        /// encode what the device produces — and it is the reason selecting a
        /// host API and settling a sample rate were never two decisions.
        /// </remarks>
        public static string DescribeRate(DeviceInfo d)
        {
            if (d == null || d.IsMissingSaved) return "";
            if (!HostApiReportsTrueRate(d.HostApiTypeId)) return "";
            double rate = d.Info.defaultSampleRate;
            if (rate <= 0) return "";
            if (d.RateCanCarryOpus) return "";
            return "Windows runs it at " + (rate / 1000.0).ToString("0.###")
                + " kHz, which cannot carry audio to or from the radio";
        }

        /// <summary>
        /// True of the host APIs that report an endpoint's real sample rate
        /// rather than a resampled one. MME and DirectSound convert silently;
        /// WASAPI and WDM-KS do not.
        /// </summary>
        public static bool HostApiReportsTrueRate(int hostApiTypeId) =>
            hostApiTypeId == WasapiTypeId || hostApiTypeId == WdmKsTypeId;

        /// <summary>
        /// Name-derived connection description. See
        /// <see cref="DeviceInfo.Connection"/> for why this is name-derived and
        /// why "built-in" is not among the answers.
        /// </summary>
        private static string ClassifyConnection(string name, int hostApiTypeId, DeviceTypes type)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string lower = name.ToLowerInvariant();

            // A WASAPI loopback input is not a microphone at all — it is
            // whatever your speakers are playing, offered back as a capture
            // device. Basic mode hides these outright (see
            // HiddenFromBasicPicker); this label is for the advanced view,
            // where the row is on screen looking exactly like a real input
            // and choosing it transmits your own received audio.
            if (IsLoopbackName(name))
                return "loopback of what this computer is playing, not a microphone";

            // The host-API default aliases. Fixed names from PortAudio's MME
            // and DirectSound backends, so this is a fact, not a guess: they
            // do not name a device, they follow whatever Windows is set to.
            if (lower == "microsoft sound mapper - input"
                || lower == "microsoft sound mapper - output"
                || lower == "primary sound capture driver"
                || lower == "primary sound driver")
            {
                return "follows your Windows default, whatever that is";
            }

            if (lower.Contains("bluetooth") || lower.Contains("bth")) return "Bluetooth";
            if (lower.Contains("hdmi")) return "HDMI";
            if (lower.Contains("displayport")) return "DisplayPort";
            if (HasWord(lower, "usb")) return "USB";
            return "";
        }

        /// <summary>Whole-word match, so "usb" does not fire inside "busbar".</summary>
        private static bool HasWord(string haystack, string word)
        {
            int at = 0;
            while ((at = haystack.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = (at == 0) || !char.IsLetterOrDigit(haystack[at - 1]);
                int end = at + word.Length;
                bool rightOk = (end >= haystack.Length) || !char.IsLetterOrDigit(haystack[end]);
                if (leftOk && rightOk) return true;
                at = end;
            }
            return false;
        }

        /// <summary>
        /// True of WASAPI loopback capture endpoints. The "[Loopback]" suffix
        /// is put there by PortAudio's WASAPI backend itself, not by any
        /// vendor, so testing for it is a fact about the endpoint rather than
        /// a guess about a naming convention.
        /// </summary>
        private static bool IsLoopbackName(string name) =>
            !string.IsNullOrEmpty(name)
            && name.TrimEnd().EndsWith("[loopback]", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when the name says the device is a virtual audio cable — a
        /// software pipe between applications, not hardware anyone talks into
        /// or listens through. Matched on the vendors' own branding, which
        /// they embed in every endpoint name ("Line 1 (Virtual Audio Cable)",
        /// "CABLE Input (VB-Audio Virtual Cable)", VoiceMeeter's VAIO
        /// endpoints), because that branding is the only stable thing about a
        /// Windows device name. Deliberately a short list of known products
        /// and nothing cleverer: a name this does not recognise is treated as
        /// real hardware, because a wrongly hidden microphone is an operator
        /// who cannot transmit and does not know why, while a wrongly shown
        /// cable is one row of clutter. Those costs are not symmetric.
        /// </summary>
        private static bool IsVirtualCableName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("virtual audio cable")   // VAC ("Line 1", "Line 2", ...)
                || lower.Contains("vb-audio")              // VB-Cable and VoiceMeeter endpoints
                || lower.Contains("voicemeeter");          // VoiceMeeter names that drop the vendor
        }

        /// <summary>
        /// True when a row stays off the basic picker. Filtering to one host
        /// API gets the list to one row per device, but on a real machine that
        /// still leaves a row for every loopback and virtual-cable endpoint —
        /// of eight inputs on the machine that prompted this, only two or three
        /// were things a person could talk into. Hidden, never removed: these
        /// rows stay enumerated, keep resolving saved selections, and come back
        /// with the advanced toggle — someone routing audio through a cable on
        /// purpose is exactly who turns that toggle on. Same argument that hid
        /// the WDM-KS kernel pins on 2026-08-11, one step further.
        ///
        /// The Windows default is exempt, whatever it is. Someone who set a
        /// virtual cable as their system-wide input did that deliberately,
        /// and a basic list with no "system default" row in it reads like a
        /// broken enumeration.
        /// </summary>
        private static bool HiddenFromBasicPicker(DeviceInfo owner)
        {
            if (owner.IsDefault || owner.GroupIsSystemDefault) return false;
            return IsLoopbackName(owner.Name) || IsVirtualCableName(owner.Name);
        }

        /// <summary>
        /// Last successful input enumeration — EVERY endpoint, one row per host
        /// API. This is the engine's view and the view saved selections resolve
        /// against; the picker shows <see cref="PickerInputDevices"/>.
        /// </summary>
        public static IReadOnlyList<DeviceInfo> InputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// Last successful output enumeration — EVERY endpoint. See
        /// <see cref="InputDevices"/>.
        /// </summary>
        public static IReadOnlyList<DeviceInfo> OutputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// The input list a person should be asked to choose from: one row per
        /// physical device, minus the rows that are not microphones at all.
        /// </summary>
        /// <remarks>
        /// A USB interface enumerates once per host API, so a single Focusrite
        /// arrives as three identical-looking choices (MME, DirectSound,
        /// WASAPI) and a multi-input interface multiplies that by its channel
        /// pairs — the development machine listed 26 input rows for what a
        /// person would call four devices. Filtering to the one host API the
        /// operator selected removes the duplicates outright; see
        /// <see cref="SelectedHostApiTypeId"/> and
        /// <see cref="SelectPickerRows"/>.
        ///
        /// The list is also filtered by kind: WASAPI loopbacks and virtual
        /// audio cables are hidden until the advanced toggle is on, because
        /// most of what Windows exposes is not a thing anyone can talk into.
        /// See <see cref="HiddenFromBasicPicker"/>.
        /// </remarks>
        public static IReadOnlyList<DeviceInfo> PickerInputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// The output list a person should be asked to choose from. See
        /// <see cref="PickerInputDevices"/>.
        /// </summary>
        public static IReadOnlyList<DeviceInfo> PickerOutputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// Channel count of the radio-audio path: the Opus stream to and from
        /// the radio is stereo, so this is what the engine (Audio.cs) opens
        /// whenever the device can supply it. Never a device filter — the old
        /// <c>StereoOnly</c> constant stood beside a channels==2 list test that
        /// silently dropped every 4-channel device, including a laptop's only
        /// real microphone array. Two or more channels open as stereo (in
        /// PortAudio's contract, which permits opening 1..max channels); a
        /// genuinely mono device opens as mono and is duplicated to stereo in
        /// the callback. See <see cref="DeviceInfo.OpenChannels"/>.
        /// </summary>
        public const int StreamChannels = 2;

        // ------------------------------------------------ host API selection

        /// <summary>PortAudio PaHostApiTypeId for DirectSound. Fixed enum value.</summary>
        public const int DirectSoundTypeId = 1;
        /// <summary>PortAudio PaHostApiTypeId for MME. Fixed enum value.</summary>
        public const int MmeTypeId = 2;
        /// <summary>PortAudio PaHostApiTypeId for WDM-KS (kernel streaming). Fixed
        /// enum value. Used to hide kernel pins from the picker by default.</summary>
        public const int WdmKsTypeId = 11;
        /// <summary>PortAudio PaHostApiTypeId for WASAPI. Fixed enum value.</summary>
        public const int WasapiTypeId = 13;

        /// <summary>
        /// What JJ Flex picks when nobody has chosen: WASAPI, the modern
        /// Windows path.
        /// </summary>
        /// <remarks>
        /// It is the honest one. MME hands every device to us through a
        /// resampler, so it reports a tidy 48 kHz whatever the hardware is
        /// really running at — which is comfortable right up until the audio
        /// matters, and is why every sample-rate test this project has run
        /// looked like a pass. WASAPI reports the endpoint's real shared-mode
        /// format and refuses a rate the endpoint cannot do. MME stays one
        /// selection away for anyone who would rather be resampled than
        /// refused, and the selector's own wording says so.
        /// </remarks>
        public const int DefaultHostApiTypeId = WasapiTypeId;

        /// <summary>One host API, as this machine reports it.</summary>
        public sealed class HostApi
        {
            /// <summary>Stable PaHostApiTypeId.</summary>
            public int TypeId;
            /// <summary>PortAudio's own name for it, e.g. "Windows WASAPI".</summary>
            public string Name = "";
            /// <summary>How many devices it offers.</summary>
            public int DeviceCount;

            /// <summary>
            /// The row a person reads: PortAudio's name plus the one sentence
            /// that decides the choice. Written to be spoken.
            /// </summary>
            public string Display
            {
                get
                {
                    switch (TypeId)
                    {
                        case WasapiTypeId:
                            return Name + " — recommended. Reports what your hardware is really doing, "
                                + "and says so when a device cannot do what the radio needs.";
                        case MmeTypeId:
                            return Name + " — the most forgiving. It converts sample rates for you, "
                                + "so devices that WASAPI refuses will usually work — but it also "
                                + "hides what rate your hardware is actually running at.";
                        case DirectSoundTypeId:
                            return Name + " — older than WASAPI, also converts sample rates. "
                                + "Worth trying if WASAPI refuses your device and MME will not do.";
                        case WdmKsTypeId:
                            return Name + " — kernel streaming, advanced. These are raw hardware "
                                + "endpoints, including jacks with nothing plugged into them.";
                        default:
                            return Name;
                    }
                }
            }
        }

        /// <summary>
        /// The host APIs this machine offers, best first. WDM-KS is present
        /// only while <see cref="ShowAdvancedDevices"/> is on.
        /// </summary>
        public static IReadOnlyList<HostApi> HostApis { get; private set; } = new List<HostApi>();

        /// <summary>
        /// The host API the picker is filtered to, as a stable
        /// PaHostApiTypeId. Set through <see cref="ApplyHostApiSelection"/> so
        /// the lists and the selection can never disagree.
        /// </summary>
        /// <remarks>
        /// <b>This filters the PICKER only.</b> <see cref="InputDevices"/> and
        /// <see cref="OutputDevices"/> stay complete, because a saved device
        /// under another host API must keep resolving — otherwise the first
        /// launch after this shipped would take an operator's working
        /// microphone away without a word, which is the exact failure this
        /// file exists to never produce.
        /// </remarks>
        public static int SelectedHostApiTypeId { get; private set; } = -1;

        /// <summary>
        /// ONE selector governs both directions, which is the DAW convention
        /// and is why this is a field on the class rather than a pair.
        /// </summary>
        /// <remarks>
        /// Settled here rather than left to emerge (Track E, 2026-08-16).
        /// Input and output were chosen separately before, and a second
        /// selector would double the tab stops in a dialog that is already
        /// long, to express a configuration — capture on one driver model,
        /// playback on another — that is legitimate but rare. The escape hatch
        /// for the rare case already exists and costs nothing: turn on
        /// <see cref="ShowAdvancedDevices"/> and every endpoint of every host
        /// API is listed, each row naming its own API, and the two lists can
        /// be set independently from there.
        /// </remarks>
        public const bool OneSelectorGovernsBothDirections = true;

        /// <summary>When true, the enumeration includes WDM-KS kernel pins and
        /// the picker shows every endpoint of every host API rather than the
        /// selected one (for power users via an "advanced devices" toggle).
        /// Default false — those pins are the trap that had operators
        /// transmitting into dead jacks.</summary>
        public static bool ShowAdvancedDevices = false;

        private string cfgFile;

        /// <summary>
        /// Chosen input device.
        /// </summary>
        public Device InputDevice
        {
            get { return configured.devs[0]; }
            private set { configured.devs[0] = value; }
        }
        /// <summary>
        /// Chosen output device.
        /// </summary>
        public Device OutputDevice
        {
            get { return configured.devs[1]; }
            private set { configured.devs[1] = value; }
        }

        /// <summary>
        /// Retrieve or select the audio device.
        /// </summary>
        /// <param name="fileName">name of config file.</param>
        public Devices(string fileName)
        {
            cfgFile = fileName;
        }

        /// <summary>
        /// Setup audio devices: enumerate the system, then load the saved
        /// selection if there is one.
        /// </summary>
        /// <returns>true on success.</returns>
        public bool Setup()
        {
            return Setup(out _, out _);
        }

        /// <summary>
        /// Setup with a reason on failure, so the caller can say something
        /// specific instead of going quiet.
        /// </summary>
        public bool Setup(out EnumerationStatus status, out string message)
        {
            status = Enumerate(out message);
            if (status != EnumerationStatus.Ok)
            {
                return false;
            }

            return LoadSavedSelection();
        }

        /// <summary>
        /// Load the saved selection without re-enumerating. For callers that
        /// have just run <see cref="Enumerate"/> themselves — a second sweep
        /// would mean a second Pa_Initialize/Pa_Terminate cycle for an answer
        /// already in hand.
        /// </summary>
        /// <returns>true when there was nothing to load, or it loaded cleanly.</returns>
        public bool LoadSavedSelection()
        {
            if (!string.IsNullOrEmpty(cfgFile) && File.Exists(cfgFile))
            {
                return readCFG();
            }

            return true;
        }

        private bool readCFG()
        {
            bool rv = true;
            Stream stream = null;
            XmlSerializer xs = null;
            try
            {
                stream = File.Open(cfgFile, FileMode.Open, FileAccess.Read,FileShare.Read);
                xs = new XmlSerializer(typeof(cfg));
                configured = (cfg)xs.Deserialize(stream);

                // Set the config file names.
                if (InputDevice != null) InputDevice.ConfigFile = cfgFile;
                if (OutputDevice != null) OutputDevice.ConfigFile = cfgFile;

                // The saved host API takes effect here rather than at
                // enumeration, because enumeration runs first and does not
                // know about any config file. Applying it rebuilds the picker
                // lists, so the caller sees a view that matches the file it
                // just read. A file written before this field existed carries
                // -1 and resolves to the default.
                ApplyHostApiSelection(configured.selectedHostApiTypeId);
            }
            catch(Exception ex)
            {
                Tracing.ErrMessageTrace(ex, true);
                rv = false;
            }
            finally
            {
                if (stream != null) stream.Dispose();
            }
            return rv;
        }

        private void writeCFG()
        {
            Stream stream = null;
            XmlSerializer xs = null;
            try
            {
                stream = File.Open(cfgFile, FileMode.Create);
                xs = new XmlSerializer(typeof(cfg));
                xs.Serialize(stream, configured);
            }
            catch(Exception ex)
            {
                Tracing.ErrMessageTrace(ex, true);
            }
            finally
            {
                if (stream != null) stream.Dispose();
            }
        }

        /// <summary>
        /// Get the configured input or output device, re-resolved against the
        /// devices the system reports right now.
        /// </summary>
        /// <param name="type">DeviceTypes value, input or output</param>
        /// <returns>the saved device, or null if it is not present</returns>
        /// <remarks>
        /// Returns null rather than raising UI. The old signature took a
        /// <c>getNew</c> flag that popped a modal picker — from a background
        /// audio thread, on the path that actually runs at connect time. The
        /// caller now handles the null and speaks.
        /// </remarks>
        public Device GetConfiguredDevice(DeviceTypes type)
        {
            Device dev = (type == DeviceTypes.input) ? InputDevice : OutputDevice;
            if ((dev == null) || !FindDevice(dev)) return null;
            return dev;
        }

        /// <summary>
        /// True when a device is saved for this type but the system no longer
        /// reports it. Distinguishes "never configured" from "the interface is
        /// unplugged", which need different words.
        /// </summary>
        public bool IsSavedDeviceMissing(DeviceTypes type, out string savedName)
        {
            Device dev = (type == DeviceTypes.input) ? InputDevice : OutputDevice;
            savedName = dev?.Name;
            if (dev == null) return false;
            return !FindDevice(dev);
        }

        /// <summary>
        /// Save a selection made in the picker. Writes through to
        /// audioDevices.xml immediately, same as the old form's OK path.
        /// </summary>
        /// <returns>the persisted device abstraction</returns>
        public Device SetConfiguredDevice(DeviceTypes type, DeviceInfo chosen)
        {
            if (chosen == null) throw new ArgumentNullException(nameof(chosen));

            int id = (type == DeviceTypes.input) ? 0 : 1;
            configured.devs[id] = new Device
            {
                Type = type,
                DevinfoID = chosen.DeviceID,
                Name = chosen.Info.name,
                hostApi = chosen.Info.hostApi,
                hostApiTypeId = chosen.HostApiTypeId,
                hostApiName = chosen.HostApiName,
                maxInputChannels = chosen.Info.maxInputChannels,
                maxOutputChannels = chosen.Info.maxOutputChannels,
                defaultLowInputLatency = chosen.Info.defaultLowInputLatency,
                defaultLowOutputLatency = chosen.Info.defaultLowOutputLatency,
                defaultHighInputLatency = chosen.Info.defaultHighInputLatency,
                defaultHighOutputLatency = chosen.Info.defaultHighOutputLatency,
                defaultSampleRate = chosen.Info.defaultSampleRate,
                ConfigFile = cfgFile
            };
            configured.selectedHostApiTypeId = SelectedHostApiTypeId;
            writeCFG();
            return configured.devs[id];
        }

        /// <summary>
        /// Persist the host-API choice on its own, for an OK that changed the
        /// audio system but left both devices alone.
        /// </summary>
        public void SaveHostApiSelection()
        {
            if (configured.selectedHostApiTypeId == SelectedHostApiTypeId) return;
            configured.selectedHostApiTypeId = SelectedHostApiTypeId;
            writeCFG();
        }

        /// <summary>The host API recorded in this config file, or -1.</summary>
        public int SavedHostApiTypeId => configured.selectedHostApiTypeId;

        /// <summary>
        /// Adopt the system default for this type, saving it like any other
        /// choice. Used by the fallback path when a saved device has vanished:
        /// the operator gets audio, and the settings surface can show what it
        /// actually fell back to.
        /// </summary>
        /// <returns>the persisted device, or null when nothing usable exists.</returns>
        /// <remarks>
        /// <para>
        /// Only devices the engine can actually open are candidates, and the
        /// operator's chosen host API is preferred over every other
        /// consideration — including PortAudio's own default flag.
        /// </para>
        /// <para>
        /// That preference is the whole point (Track E, 2026-08-16). PortAudio
        /// nominates a default device, and on the development machine that
        /// nomination was the MME endpoint; a fallback that simply took it
        /// would put an operator who had deliberately selected WASAPI onto MME
        /// without a word, which is exactly the silent host-API choice this
        /// work exists to remove. The Windows default still wins among the
        /// endpoints of the selected API, because "the one Windows uses" is a
        /// good answer to "which, if you do not care".
        /// </para>
        /// </remarks>
        public Device AdoptSystemDefault(DeviceTypes type)
        {
            var list = (type == DeviceTypes.input) ? InputDevices : OutputDevices;
            DeviceInfo pick = null;        // best so far on the selected host API
            DeviceInfo anyApi = null;      // best so far anywhere
            for (int i = 0; i < list.Count; i++)
            {
                DeviceInfo d = list[i];
                if (!d.UsableForRadioAudio) continue;
                if (anyApi == null || (d.IsDefault && !anyApi.IsDefault)) anyApi = d;
                if (SelectedHostApiTypeId >= 0 && d.HostApiTypeId != SelectedHostApiTypeId) continue;
                if (pick == null) pick = d;
                if (d.IsDefault) { pick = d; break; }
            }
            pick ??= anyApi;
            if (pick == null) return null;
            Tracing.TraceLine("Devices.AdoptSystemDefault: " + type + " falling back to \""
                + pick.Name + "\" (" + pick.HostApiName + ", " + pick.NativeChannels
                + " channel(s), " + pick.Info.defaultSampleRate + " Hz)", TraceLevel.Info);
            return SetConfiguredDevice(type, pick);
        }

        /// <summary>
        /// Enumerate the system's audio devices into
        /// <see cref="InputDevices"/> / <see cref="OutputDevices"/>.
        /// UI-free: no MessageBox, no dialogs, safe from any thread.
        /// </summary>
        /// <param name="message">
        /// Human-readable explanation when the result is not Ok. Written to be
        /// spoken as-is.
        /// </param>
        /// <remarks>
        /// A snapshot, not a subscription — PortAudio is initialised, walked,
        /// and terminated. Devices hot-plugged after this call are invisible
        /// until it runs again, which is why the picker has a Refresh button.
        ///
        /// Every device with channels in a direction is listed in that
        /// direction's list — channel count is never a filter. See
        /// <see cref="DeviceInfo.UsableForRadioAudio"/> for which of them the
        /// engine can open today.
        /// </remarks>
        public static EnumerationStatus Enumerate(out string message)
        {
            lock (EnumerationLock)
            {
                return EnumerateLocked(out message);
            }
        }

        // Pa_Initialize / Pa_Terminate are reference-counted but not
        // thread-safe against each other, and enumeration can now be asked for
        // from the UI (the picker's Refresh button) while the audio thread is
        // doing its own setup. Serialising our own calls is cheap; it does not
        // protect against PortAudio work started elsewhere in the process, which
        // is a pre-existing condition, not one introduced here.
        private static readonly object EnumerationLock = new object();

        /// <summary>
        /// The lock every Pa_Initialize / Pa_Terminate pair in this assembly
        /// takes. Exposed (Mic Track, 2026-08-12) so <see cref="MicProbe"/>
        /// mutates the same reference count under the same lock this class
        /// does — the probe holds an initialisation open for the length of a
        /// microphone check, which is safe precisely because the count is
        /// reference-counted, and unsafe if two threads race the count itself.
        /// </summary>
        internal static object PortAudioLifecycleLock => EnumerationLock;

        private static EnumerationStatus EnumerateLocked(out string message)
        {
            message = "";
            var inputs = new List<DeviceInfo>();
            var outputs = new List<DeviceInfo>();
            // Host APIs that actually carry a device on this machine. Built
            // from the endpoints we keep rather than from Pa_GetHostApiCount,
            // so the selector can never offer an API with nothing behind it —
            // and so WDM-KS, which is skipped outright below unless the
            // advanced toggle is on, stays out of the selector for the same
            // reason it stays out of the lists.
            var apis = new Dictionary<int, HostApi>();

            PortAudio.PaError perr;
            if ((perr = PortAudio.Pa_Initialize()) != 0)
            {
                message = "The audio system would not start: " + PortAudio.Pa_GetErrorText(perr);
                Tracing.TraceLine("Devices.Enumerate: Pa_Initialize failed, " + message, TraceLevel.Error);
                return EnumerationStatus.InitFailed;
            }

            try
            {
                int numDevs = PortAudio.Pa_GetDeviceCount();
                if (numDevs < 0)
                {
                    perr = (PortAudio.PaError)numDevs;
                    message = "The audio system could not list your sound devices: " + PortAudio.Pa_GetErrorText(perr);
                    Tracing.TraceLine("Devices.Enumerate: Pa_GetDeviceCount failed, " + message, TraceLevel.Error);
                    return EnumerationStatus.QueryFailed;
                }

                int defaultInputId = PortAudio.Pa_GetDefaultInputDevice();
                int defaultOutputId = PortAudio.Pa_GetDefaultOutputDevice();

                for (int i = 0; i < numDevs; i++)
                {
                    PortAudio.PaDeviceInfo pinfo = PortAudio.Pa_GetDeviceInfo(i);
                    if (string.IsNullOrEmpty(pinfo.name)) continue;

                    string apiName = "";
                    int apiTypeId = -1;
                    try
                    {
                        PortAudio.PaHostApiInfo api = PortAudio.Pa_GetHostApiInfo(pinfo.hostApi);
                        apiName = api.name ?? "";
                        apiTypeId = (int)api.type;
                    }
                    catch (Exception ex)
                    {
                        // A missing host-API record is not fatal — the device is
                        // still selectable, it just loses the stable id.
                        Tracing.TraceLine("Devices.Enumerate: host api info failed for device " + i
                            + ", " + ex.Message, TraceLevel.Info);
                    }

                    // Log every endpoint by name/API/channels/RATE — the one line
                    // that lets us diagnose a bad device pick from a trace (a
                    // mono or multi-channel mic hidden by the stereo filter is
                    // otherwise invisible in every other log).
                    //
                    // The rate was missing until 2026-08-24, and its absence cost
                    // a diagnosis that same evening: a device row reading "cannot
                    // carry audio to or from the radio", and the picker's advice
                    // to fall back to MME, are BOTH decided by this number, and
                    // neither the number nor the decision reached the trace. The
                    // question "why is it telling me MME?" could not be answered
                    // from a 256 KB trace of the exact session that asked it.
                    //
                    // Say the verdict too, not just the figure. A reader who
                    // knows Opus takes 8/12/16/24/48 kHz can derive it; a reader
                    // who does not is exactly the reader the line is for. And
                    // MME and DirectSound RESAMPLE, so their figure is a polite
                    // fiction — marked as such rather than left to mislead.
                    string rateNote;
                    if (!HostApiReportsTrueRate(apiTypeId))
                        rateNote = " rate=" + pinfo.defaultSampleRate + " (converted by " + apiName + ", not the hardware rate)";
                    else if (AudioAnchor.isOpusRate((uint)pinfo.defaultSampleRate))
                        rateNote = " rate=" + pinfo.defaultSampleRate;
                    else
                        rateNote = " rate=" + pinfo.defaultSampleRate + " CANNOT CARRY RADIO AUDIO";

                    Tracing.TraceLine("Devices.Enumerate: dev " + i + ": \"" + pinfo.name + "\" api=" + apiName
                        + " in=" + pinfo.maxInputChannels + " out=" + pinfo.maxOutputChannels
                        + rateNote
                        + ((i == defaultInputId) ? " [default in]" : "")
                        + ((i == defaultOutputId) ? " [default out]" : ""), TraceLevel.Info);

                    // Hide WDM-KS kernel pins by default (2026-08-11). They expose
                    // raw hardware endpoints — often a dead physical jack — under
                    // pristine, un-truncated names, so they LOOK like the best pick
                    // in the list and are the worst: a field case had two operators
                    // each select a KS pin to a jack with nothing plugged in and
                    // transmit silence. Every real endpoint also appears under
                    // MME/DirectSound/WASAPI, so nothing is lost. ShowAdvancedDevices
                    // brings them back for power users.
                    if (apiTypeId == WdmKsTypeId && !ShowAdvancedDevices)
                    {
                        Tracing.TraceLine("Devices.Enumerate: hiding WDM-KS device \"" + pinfo.name + "\"", TraceLevel.Info);
                        continue;
                    }

                    // Channel policy (Picker Track, 2026-08-12): list EVERY
                    // device that has channels in the direction — the count is
                    // capability, never a filter. A channels==2 test here
                    // survived the enumeration fix and silently dropped
                    // exactly the 4-channel devices, among them a laptop's
                    // only real internal microphone (in=4 under MME,
                    // DirectSound AND WASAPI), leaving the operator with no
                    // selectable mic while the per-device trace lines above
                    // showed it enumerating perfectly. Trace said "22 input"
                    // while 26 non-WDM-KS inputs enumerated; the missing four
                    // were precisely the in=4 rows, and nothing logged why.
                    //
                    // Devices with more than two channels need no code to
                    // work: the engine opens streams at StreamChannels==2,
                    // and PortAudio's documented contract accepts any channel
                    // count from 1 to the device's maximum — the two-channel
                    // open IS the downmix. Since 2026-08-16 (Track E) mono
                    // devices work too: the engine opens them at one channel
                    // and duplicates to stereo in the callback, so the count
                    // is capability in both directions rather than a ceiling
                    // with a floor under it.
                    if (apiTypeId >= 0 && (pinfo.maxInputChannels >= 1 || pinfo.maxOutputChannels >= 1))
                    {
                        if (!apis.TryGetValue(apiTypeId, out HostApi api))
                        {
                            api = new HostApi { TypeId = apiTypeId, Name = apiName };
                            apis[apiTypeId] = api;
                        }
                        api.DeviceCount++;
                    }

                    if (pinfo.maxInputChannels >= 1)
                    {
                        inputs.Add(new DeviceInfo
                        {
                            Info = pinfo,
                            Type = DeviceTypes.input,
                            DeviceID = i,
                            IsDefault = (i == defaultInputId),
                            HostApiTypeId = apiTypeId,
                            HostApiName = apiName
                        });
                    }

                    if (pinfo.maxOutputChannels >= 1)
                    {
                        outputs.Add(new DeviceInfo
                        {
                            Info = pinfo,
                            Type = DeviceTypes.output,
                            DeviceID = i,
                            IsDefault = (i == defaultOutputId),
                            HostApiTypeId = apiTypeId,
                            HostApiName = apiName
                        });
                    }
                }
            }
            finally
            {
                PortAudio.Pa_Terminate();
            }

            // The system default sorts first in each list. Everything else keeps
            // PortAudio's order, so a list a user has learned stays learned.
            MoveDefaultFirst(inputs);
            MoveDefaultFirst(outputs);

            InputDevices = inputs;
            OutputDevices = outputs;

            // The identity index first — which endpoints are the same piece of
            // hardware — because the picker view and the Windows level control
            // both read it. Then the view itself, which is a filter over the
            // full lists and never a replacement for them.
            BuildGroups(inputs);
            BuildGroups(outputs);

            var apiList = new List<HostApi>(apis.Values);
            apiList.Sort((a, b) =>
            {
                int r = HostApiRank(a.TypeId).CompareTo(HostApiRank(b.TypeId));
                return (r != 0) ? r : string.CompareOrdinal(a.Name, b.Name);
            });
            HostApis = apiList;
            var apiSummary = new StringBuilder("Devices.Enumerate: host APIs:");
            foreach (HostApi a in apiList)
                apiSummary.Append(' ').Append(a.Name).Append('=').Append(a.DeviceCount);
            Tracing.TraceLine(apiSummary.ToString(), TraceLevel.Info);

            ApplyHostApiSelection(SelectedHostApiTypeId);

            if (inputs.Count == 0 && outputs.Count == 0)
            {
                message = "No audio devices were detected. Attach or enable an input and output audio device and choose Refresh.";
                Tracing.TraceLine("Devices.Enumerate: no devices", TraceLevel.Error);
                return EnumerationStatus.NoDevices;
            }

            // Count capability explicitly. The regression that hid the
            // 4-channel devices was diagnosed from this very line reading
            // "22 input" while 26 inputs enumerated — from now on the summary
            // says both how many are listed and how many the engine can open,
            // so a capability gap is visible in one line of trace.
            int usableIn = inputs.Count(d => d.UsableForRadioAudio);
            int usableOut = outputs.Count(d => d.UsableForRadioAudio);
            int monoIn = inputs.Count(d => d.IsMono);
            int monoOut = outputs.Count(d => d.IsMono);
            Tracing.TraceLine("Devices.Enumerate: " + inputs.Count + " input ("
                + usableIn + " openable, " + monoIn + " mono), " + outputs.Count + " output ("
                + usableOut + " openable, " + monoOut + " mono)", TraceLevel.Info);
            return EnumerationStatus.Ok;
        }

        // ------------------------------- host API selection and picker view

        /// <summary>
        /// Choose the host API the picker shows, and rebuild the picker lists
        /// around it.
        /// </summary>
        /// <param name="typeId">
        /// A PaHostApiTypeId, or -1 for "nobody has chosen". Either way the
        /// answer is resolved against the APIs this machine actually reports,
        /// so a saved choice for a driver model that is no longer present
        /// falls through to one that is instead of emptying the lists.
        /// </param>
        /// <returns>the type id actually in force afterwards.</returns>
        public static int ApplyHostApiSelection(int typeId)
        {
            int resolved = ResolveHostApi(typeId);
            if (resolved != SelectedHostApiTypeId)
            {
                Tracing.TraceLine("Devices.ApplyHostApiSelection: audio system is "
                    + NameOfHostApi(resolved)
                    + (typeId == resolved ? "" : " (asked for " + NameOfHostApi(typeId) + ")"),
                    TraceLevel.Info);
            }
            SelectedHostApiTypeId = resolved;
            RebuildPickerLists();
            return resolved;
        }

        /// <summary>
        /// The host API to use, given what was asked for and what exists.
        /// Preference order: the request, then the default (WASAPI), then
        /// whatever ranks best on this machine.
        /// </summary>
        private static int ResolveHostApi(int typeId)
        {
            IReadOnlyList<HostApi> apis = HostApis;
            if (apis == null || apis.Count == 0) return typeId;

            foreach (HostApi a in apis) if (a.TypeId == typeId) return typeId;
            foreach (HostApi a in apis) if (a.TypeId == DefaultHostApiTypeId) return DefaultHostApiTypeId;
            // HostApis is already sorted best-first.
            return apis[0].TypeId;
        }

        /// <summary>PortAudio's name for a host API, or a legible stand-in.</summary>
        public static string NameOfHostApi(int typeId)
        {
            foreach (HostApi a in HostApis) if (a.TypeId == typeId) return a.Name;
            switch (typeId)
            {
                case WasapiTypeId: return "Windows WASAPI";
                case DirectSoundTypeId: return "Windows DirectSound";
                case MmeTypeId: return "MME";
                case WdmKsTypeId: return "Windows WDM-KS";
                default: return "host API " + typeId;
            }
        }

        /// <summary>
        /// Rebuild <see cref="PickerInputDevices"/> and
        /// <see cref="PickerOutputDevices"/> from the full enumeration, for the
        /// host API and advanced setting currently in force.
        /// </summary>
        public static void RebuildPickerLists()
        {
            PickerInputDevices = SelectPickerRows(InputDevices);
            PickerOutputDevices = SelectPickerRows(OutputDevices);
        }

        /// <summary>
        /// MME device names are truncated by Windows to 31 characters
        /// (MAXPNAMELEN is 32 including the terminator). Confirmed against a
        /// live trace, where "Mic | Line | Instrument 1 (Audient EVO8)" arrived
        /// from DirectSound and WASAPI in full and from MME as
        /// "Mic | Line | Instrument 1 (Audi" — exactly 31 characters. Without
        /// allowing for it, an exact-name grouping leaves every long-named
        /// device with a stray MME twin that looks like a second device.
        /// </summary>
        private const int MmeNameLimit = 31;

        /// <summary>
        /// Preference among host APIs when one physical device offers several.
        /// WASAPI is the modern Windows path and reports device names in full;
        /// DirectSound next; MME last of the real three because it is the one
        /// that truncates names. WDM-KS ranks below everything — those are raw
        /// kernel pins, hidden entirely unless the advanced toggle is on, and
        /// they must never be chosen as a group's representative when any
        /// ordinary endpoint exists.
        /// </summary>
        private static int HostApiRank(int hostApiTypeId)
        {
            switch (hostApiTypeId)
            {
                case WasapiTypeId: return 0;
                case DirectSoundTypeId: return 1;
                case MmeTypeId: return 2;
                case WdmKsTypeId: return 4;
                default: return 3;
            }
        }

        /// <summary>
        /// Work out which endpoints are the same piece of hardware, and record
        /// it on every row: <see cref="DeviceInfo.GroupOwner"/>,
        /// <see cref="DeviceInfo.Alternates"/> and
        /// <see cref="DeviceInfo.GroupIsSystemDefault"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Grouping is by device name, normalised for case and whitespace, plus
        /// one extra rule for MME truncation (see <see cref="MmeNameLimit"/>): a
        /// short name that is a prefix of exactly ONE longer name is the same
        /// device seen through MME. "Exactly one" matters — where a prefix
        /// matches several longer names the answer is genuinely ambiguous, and
        /// merging on a guess would hide a real device behind an unrelated one,
        /// so an ambiguous prefix stays its own group.
        /// </para>
        /// <para>
        /// Channel counts are NOT part of the key. The same interface reports
        /// different channel counts under different host APIs, and one physical
        /// device is one piece of hardware regardless.
        /// </para>
        /// <para>
        /// Track E, 2026-08-16: this used to also DECIDE the picker — one row
        /// per group, the rest hidden — which is how a host API got chosen on
        /// the operator's behalf. It is now an index and nothing more. Two
        /// callers still need it: <c>WindowsMicLevel</c> matches a PortAudio
        /// row to a Core Audio endpoint by trying every name the hardware goes
        /// by, and the Windows-default flag belongs to hardware rather than to
        /// whichever endpoint PortAudio tagged.
        /// </para>
        /// </remarks>
        private static void BuildGroups(List<DeviceInfo> all)
        {
            // Every row starts as its own group owner, so nothing is ever left
            // with a null owner even if grouping bails out below.
            foreach (DeviceInfo d in all)
            {
                d.GroupOwner = d;
                d.Alternates = new List<DeviceInfo>();
            }

            var byName = new Dictionary<string, List<DeviceInfo>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (DeviceInfo d in all)
            {
                string key = NormalizeName(d.Name);
                if (!byName.TryGetValue(key, out List<DeviceInfo> bucket))
                {
                    bucket = new List<DeviceInfo>();
                    byName[key] = bucket;
                    order.Add(key);
                }
                bucket.Add(d);
            }

            // Fold truncated MME names into their full-length twin.
            foreach (string shortKey in new List<string>(order))
            {
                // A truncated MME name is 31 characters; allow 30 too, because
                // normalising trims a trailing space off a name that was cut
                // mid-word. Anything shorter was never truncated.
                if (shortKey.Length < MmeNameLimit - 1) continue;
                if (!byName.TryGetValue(shortKey, out List<DeviceInfo> shortBucket)) continue;
                if (shortBucket.Count == 0) continue;
                // Only a bucket made up ENTIRELY of MME rows can be a
                // truncation artefact; anything else is a real device that
                // simply has a long name.
                bool allMme = shortBucket.TrueForAll(d => d.HostApiTypeId == MmeTypeId);
                if (!allMme) continue;

                string target = null;
                int hits = 0;
                foreach (string candidate in order)
                {
                    if (ReferenceEquals(candidate, shortKey) || candidate == shortKey) continue;
                    if (candidate.Length <= shortKey.Length) continue;
                    if (!candidate.StartsWith(shortKey, StringComparison.Ordinal)) continue;
                    if (!byName.TryGetValue(candidate, out List<DeviceInfo> t) || t.Count == 0) continue;
                    target = candidate;
                    if (++hits > 1) break;
                }
                if (hits != 1 || target == null)
                {
                    if (hits > 1)
                    {
                        Tracing.TraceLine("Devices.BuildGroups: \"" + shortBucket[0].Name
                            + "\" is a prefix of more than one device name; leaving it as its own group",
                            TraceLevel.Info);
                    }
                    continue;
                }

                byName[target].AddRange(shortBucket);
                shortBucket.Clear();
            }

            foreach (string key in order)
            {
                List<DeviceInfo> bucket = byName[key];
                if (bucket.Count == 0) continue;   // merged into another group

                DeviceInfo owner = ChooseRepresentative(bucket);
                var alternates = new List<DeviceInfo>();
                foreach (DeviceInfo d in bucket)
                {
                    d.GroupOwner = owner;
                    if (!ReferenceEquals(d, owner)) alternates.Add(d);
                }
                owner.Alternates = alternates;

                // The Windows default belongs to the physical device, not to
                // the endpoint that happened to carry the flag — and every
                // endpoint of it carries the fact now, because with the list
                // filtered by host API any of them can be the row on screen.
                bool anyDefault = false;
                foreach (DeviceInfo d in bucket)
                {
                    if (d.IsDefault) { anyDefault = true; break; }
                }
                foreach (DeviceInfo d in bucket) d.GroupIsSystemDefault = anyDefault;
            }
        }

        /// <summary>
        /// The rows a person should choose from: every endpoint of the selected
        /// host API, minus the rows nobody can talk into (see
        /// <see cref="HiddenFromBasicPicker"/>). The advanced view drops the
        /// host-API filter and shows every endpoint of every API.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is where a picker got SMALLER by adding a control. It used to
        /// fold every endpoint of a device into one row, because a USB
        /// interface arrives once per host API and the development machine
        /// listed 26 input rows for what a person would call four devices.
        /// Folding solved the list length and created a worse problem: some
        /// endpoint had to be picked to stand for the rest, so the app was
        /// choosing a driver model silently — and the tie-breaks landed on MME
        /// often enough to matter. MME resamples on the way through, so it
        /// reports 48 kHz for hardware running at anything, and a rate problem
        /// could not be seen from inside the app at all.
        /// </para>
        /// <para>
        /// Selecting the host API first removes the duplicates rather than
        /// hiding them: one API, one row per device, no representative to
        /// choose and nothing folded away. Same 26 endpoints, roughly the same
        /// number of rows as folding produced, and the reason each row is there
        /// is now a thing the operator set.
        /// </para>
        /// </remarks>
        private static List<DeviceInfo> SelectPickerRows(IReadOnlyList<DeviceInfo> all)
        {
            var picker = new List<DeviceInfo>();
            if (all == null) return picker;

            if (ShowAdvancedDevices)
            {
                // Every endpoint of every host API, kernel pins included. This
                // is also the escape hatch for the rare operator who wants
                // capture on one driver model and playback on another: the
                // single selector governs the basic lists, and this view is
                // where the two can be set apart.
                Tracing.TraceLine("Devices.SelectPickerRows: advanced view, showing all "
                    + all.Count + " endpoints", TraceLevel.Info);
                picker.AddRange(all);
                SortForPicker(picker);
                return picker;
            }

            var hidden = new List<DeviceInfo>();
            var otherApi = 0;
            foreach (DeviceInfo d in all)
            {
                if (SelectedHostApiTypeId >= 0 && d.HostApiTypeId != SelectedHostApiTypeId)
                {
                    otherApi++;
                    continue;
                }

                // Hide what nobody can talk into — see HiddenFromBasicPicker.
                // The row is off the menu, not gone: it stays in InputDevices /
                // OutputDevices, so a saved selection still resolves to it. The
                // trace line is not optional decoration — every
                // silently-hidden-device bug this file has ever had was
                // diagnosed from these lines, or dragged on because one was
                // missing.
                if (HiddenFromBasicPicker(d))
                {
                    Tracing.TraceLine("Devices.SelectPickerRows: hiding \"" + d.Name
                        + "\" (" + (IsLoopbackName(d.Name) ? "loopback" : "virtual cable") + ")",
                        TraceLevel.Info);
                    hidden.Add(d);
                    continue;
                }

                picker.Add(d);
            }

            // If the filter is the only reason the list is empty, the filter
            // loses. On a machine whose inputs really are all virtual cables
            // (streaming rigs, VMs), the cable is the closest thing to a
            // microphone there is, and an empty picker while devices enumerate
            // would read as "no audio devices" — a lie, and the exact kind of
            // silent disappearance this file exists to never produce.
            if (picker.Count == 0 && hidden.Count > 0)
            {
                Tracing.TraceLine("Devices.SelectPickerRows: the basic-mode filter hid every device; "
                    + "showing all " + hidden.Count + " rather than an empty list", TraceLevel.Info);
                picker.AddRange(hidden);
                hidden.Clear();
            }

            SortForPicker(picker);

            Tracing.TraceLine("Devices.SelectPickerRows: " + all.Count + " endpoints, "
                + picker.Count + " shown under " + NameOfHostApi(SelectedHostApiTypeId)
                + (otherApi > 0 ? ", " + otherApi + " on other host APIs" : "")
                + (hidden.Count > 0 ? ", " + hidden.Count + " hidden (loopback/virtual cable)" : ""),
                TraceLevel.Info);
            return picker;
        }

        /// <summary>
        /// Put the picker's rows in the order a person would look for them:
        /// by device name, counting digits as numbers, then by host API
        /// preference, then by the system's own index so the order is total
        /// and stable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why order at all (#213).</b> The lists arrived in PortAudio's
        /// enumeration order, which is driver and registry order — nothing an
        /// operator can predict, and nothing that keeps related rows together.
        /// An interface with numbered ports is the worst case: Line 1, Line 2,
        /// Line 3 and Line 4 scatter through the list with unrelated devices
        /// between them.
        /// </para>
        /// <para>
        /// This costs a sighted operator almost nothing — they scan the list
        /// and find the row — and it costs a screen-reader operator the whole
        /// list, because arrowing is linear and TYPE-AHEAD is the fast path.
        /// Type-ahead only works if the rows a letter matches are together;
        /// an unsorted list turns first-letter navigation into a lottery.
        /// </para>
        /// <para>
        /// <b>Digits count as numbers, not characters.</b> Plain alphabetical
        /// order puts Line 10 between Line 1 and Line 2, which is a visible
        /// half-measure on exactly the hardware this exists for.
        /// </para>
        /// <para>
        /// <b>The system default is NOT hoisted to the top, and that is a
        /// decision.</b> <see cref="DeviceInfo.Display"/> already calls it out
        /// in words, for the reason recorded there: "first in the list" is not
        /// information you can hear. Hoisting it would also break the sorted
        /// invariant that type-ahead depends on, to convey something the row
        /// already says out loud. What the default must NOT lose is being the
        /// FALLBACK selection when nothing is saved — that used to fall out of
        /// list position and now has to be asked for by name; see the picker
        /// dialog's DefaultOrFirstUsableIndex.
        /// </para>
        /// <para>
        /// <b>Nothing here folds or hides.</b> The host-API filter and the
        /// hidden-kind filter above have already decided WHICH rows exist; this
        /// only decides their order. In the advanced view, where one physical
        /// device legitimately appears once per host API, sorting by name is
        /// what finally puts those endpoints next to each other instead of
        /// scattering them — and the host-API tie-break then lists them in a
        /// consistent, preference order rather than enumeration order.
        /// </para>
        /// </remarks>
        private static void SortForPicker(List<DeviceInfo> rows)
        {
            if (rows == null || rows.Count < 2) return;
            rows.Sort(ComparePickerRows);
        }

        /// <summary>Total order over picker rows: name, then host API, then index.</summary>
        private static int ComparePickerRows(DeviceInfo a, DeviceInfo b)
        {
            int byName = CompareDeviceNames(a?.Name, b?.Name);
            if (byName != 0) return byName;

            int byApi = HostApiRank(a?.HostApiTypeId ?? -1)
                .CompareTo(HostApiRank(b?.HostApiTypeId ?? -1));
            if (byApi != 0) return byApi;

            return (a?.DeviceID ?? -1).CompareTo(b?.DeviceID ?? -1);
        }

        /// <summary>
        /// Compare two device names the way a person reads them: case
        /// insensitively, and treating a run of digits as one number rather
        /// than as characters, so Line 2 comes before Line 10.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ordinal rather than culture-aware on purpose. This order is what
        /// type-ahead lands on, so it has to be the same on every machine and
        /// in every locale; a culture-sensitive collation would reorder the
        /// list for some operators and not others, and the one thing a
        /// keyboard operator is entitled to is that the list does not move.
        /// </para>
        /// <para>
        /// Digit runs are compared by value, with leading zeros ignored, and
        /// a longer run of significant digits is the larger number. Equal
        /// values fall through to the next segment rather than declaring the
        /// names equal, so "Line 01" and "Line 1" still order deterministically
        /// by what follows.
        /// </para>
        /// </remarks>
        public static int CompareDeviceNames(string a, string b)
        {
            a ??= "";
            b ??= "";

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool aDigit = char.IsDigit(a[i]);
                bool bDigit = char.IsDigit(b[j]);

                if (aDigit && bDigit)
                {
                    int aStart = i, bStart = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    // Skip leading zeros so 007 and 7 are the same number.
                    int aZeros = aStart;
                    while (aZeros < i - 1 && a[aZeros] == '0') aZeros++;
                    int bZeros = bStart;
                    while (bZeros < j - 1 && b[bZeros] == '0') bZeros++;

                    int aLen = i - aZeros, bLen = j - bZeros;
                    if (aLen != bLen) return aLen < bLen ? -1 : 1;

                    int digits = string.CompareOrdinal(a, aZeros, b, bZeros, aLen);
                    if (digits != 0) return digits < 0 ? -1 : 1;
                    continue;
                }

                if (aDigit != bDigit)
                {
                    // A digit sorts before a letter, which is what ordinal
                    // comparison would have done anyway; being explicit keeps
                    // the two branches from disagreeing.
                    return aDigit ? -1 : 1;
                }

                char ca = char.ToUpperInvariant(a[i]);
                char cb = char.ToUpperInvariant(b[j]);
                if (ca != cb) return ca < cb ? -1 : 1;
                i++;
                j++;
            }

            // One name is a prefix of the other: the shorter comes first. This
            // is also what puts an MME-truncated twin immediately before the
            // full-length name it was cut from.
            int remaining = (a.Length - i).CompareTo(b.Length - j);
            if (remaining != 0) return remaining;

            // Same letters, different case only. Order by the raw text so the
            // comparison is a total order rather than reporting equality for
            // names that are not equal.
            return string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// Pick the endpoint that speaks for a physical device: best host API
        /// first, then the one the engine can actually open, then the Windows
        /// default.
        /// </summary>
        private static DeviceInfo ChooseRepresentative(List<DeviceInfo> bucket)
        {
            DeviceInfo best = null;
            foreach (DeviceInfo d in bucket)
            {
                if (best == null) { best = d; continue; }

                int dRank = HostApiRank(d.HostApiTypeId);
                int bestRank = HostApiRank(best.HostApiTypeId);
                if (dRank != bestRank)
                {
                    if (dRank < bestRank) best = d;
                    continue;
                }

                if (d.IsDefault && !best.IsDefault) best = d;
            }
            return best;
        }

        /// <summary>Case- and whitespace-insensitive form of a device name.</summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool lastWasSpace = false;
            foreach (char c in name.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The saved device's own row in the picker, or null when the picker's
        /// current view does not contain it (a different host API is selected,
        /// or it is a loopback or virtual cable in the basic view).
        /// </summary>
        /// <remarks>
        /// This used to hop to the group's representative, because the picker
        /// showed one row per physical device and a selection saved under one
        /// host API had to land on whichever endpoint was standing in for it.
        /// With the list filtered to a host API the operator chose, the saved
        /// endpoint is either in the list or it is not — and "not" is worth
        /// saying rather than papering over, because it means the audio system
        /// setting and the saved device disagree. The caller keeps the saved
        /// row on screen and explains; see the picker dialog's
        /// SelectFilteredSavedRow.
        /// </remarks>
        public static DeviceInfo FindPickerRow(Device saved)
        {
            DeviceInfo live = FindLive(saved);
            if (live == null) return null;
            IReadOnlyList<DeviceInfo> rows = (live.Type == DeviceTypes.input)
                ? PickerInputDevices : PickerOutputDevices;
            for (int i = 0; i < rows.Count; i++)
            {
                if (ReferenceEquals(rows[i], live)) return live;
            }
            return null;
        }

        /// <summary>
        /// True when <paramref name="row"/> is the endpoint already saved — so
        /// a picker OK on an unchanged selection writes nothing.
        /// </summary>
        /// <remarks>
        /// Endpoint identity, not hardware identity, since 2026-08-16. While
        /// the picker folded a device's endpoints into one row, hardware was
        /// the only identity the operator could express and rewriting a saved
        /// MME endpoint to its WASAPI twin on an OK nobody meant as a change
        /// would have moved a working configuration silently. Now the host API
        /// is a thing they set on purpose: choosing the WASAPI row for hardware
        /// currently saved under MME IS the change they came here to make, and
        /// treating it as a no-op would make the selector do nothing.
        /// </remarks>
        public static bool SameDevice(Device saved, DeviceInfo row)
        {
            if (saved == null || row == null || row.IsMissingSaved) return false;

            DeviceInfo savedLive = FindLive(saved);
            if (savedLive == null) return false;
            return ReferenceEquals(savedLive, row);
        }

        /// <summary>
        /// A stand-in row for a saved device the system no longer reports, so
        /// an unplugged interface says so in the list rather than vanishing
        /// from it. Not openable, never handed to the audio engine, and never
        /// part of <see cref="InputDevices"/> / <see cref="OutputDevices"/>.
        /// </summary>
        public static DeviceInfo MissingSavedRow(Device saved)
        {
            if (saved == null) return null;
            return new DeviceInfo
            {
                Info = new PortAudio.PaDeviceInfo(),
                Type = saved.Type,
                DeviceID = -1,
                IsDefault = false,
                IsMissingSaved = true,
                SavedDevice = saved,
                HostApiTypeId = saved.hostApiTypeId,
                HostApiName = saved.hostApiName ?? ""
            };
        }

        private static void MoveDefaultFirst(List<DeviceInfo> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsDefault) continue;
                if (i == 0) return;
                DeviceInfo d = list[i];
                list.RemoveAt(i);
                list.Insert(0, d);
                return;
            }
        }

        /// <summary>
        /// Re-resolve a saved device against the current enumeration and update
        /// its <see cref="Device.DevinfoID"/>.
        /// </summary>
        /// <param name="arg">Device to match, may be null.</param>
        /// <returns>true if found; arg.DevinfoID is set.</returns>
        /// <remarks>
        /// PortAudio indexes are positional: plug in a USB interface and every
        /// later index shifts, so the DevinfoID stored in audioDevices.xml can
        /// silently point at a different physical device the next run. Binding
        /// on it would repoint the microphone with no error and no
        /// announcement — the worst failure this file can produce. So the
        /// saved index NEVER creates a match. Identity is name plus host API:
        ///
        ///   1. name + host API type id — the strict pass, used when the saved
        ///      entry carries the type id (written since 2026-08-07). Channel
        ///      counts are deliberately NOT part of identity: they are a
        ///      capability, and a driver update or a Windows format change
        ///      that turns a 2-channel mic into a 4-channel one should keep
        ///      the operator's device, not silently discard their choice.
        ///   2. name + channel counts — the pre-2026-08-07 rule, kept so old
        ///      config files resolve unchanged. Also the answer when a host
        ///      API disappears from the system entirely.
        ///
        /// The saved index serves exactly one purpose: when several live rows
        /// match equally (two identically named devices under one host API),
        /// it breaks the tie toward the row at the remembered position.
        ///
        /// Only rows the engine can open (<see
        /// cref="DeviceInfo.UsableForRadioAudio"/>) can bind, so this can
        /// never hand the stream path a device whose open must fail.
        ///
        /// A device that matches is the device the user picked, so rebinding
        /// is silent. Only a genuine no-match returns false, and that is the
        /// one case the caller announces — never a silent substitution.
        /// </remarks>
        public static bool FindDevice(Device arg)
        {
            DeviceInfo hit = FindLive(arg);
            if (hit == null) return false;
            arg.DevinfoID = hit.DeviceID;
            arg.hostApi = hit.Info.hostApi;

            // Refresh the hardware facts from the live device rather than
            // carrying the ones that were true when the file was written.
            //
            // Track E, 2026-08-16. Identity is name plus host API, and channel
            // counts are deliberately NOT part of it — a driver update that
            // turns a 2-channel mic into a 4-channel one should keep the
            // operator's device, not discard their choice. That is right, and
            // it means a saved record can carry a channel count the hardware no
            // longer reports. The engine opens at the saved count, so a device
            // that dropped from two channels to one would still be asked for
            // two and PortAudio would refuse — putting the mono failure back
            // through the side door on exactly the devices this release fixed.
            // The rate and latency figures go stale the same way, and both feed
            // rate negotiation and the stream parameters.
            //
            // The file is not rewritten here. These values only reach disk if
            // the operator saves for some other reason, at which point they are
            // the correct ones to store anyway.
            arg.maxInputChannels = hit.Info.maxInputChannels;
            arg.maxOutputChannels = hit.Info.maxOutputChannels;
            arg.defaultSampleRate = hit.Info.defaultSampleRate;
            arg.defaultLowInputLatency = hit.Info.defaultLowInputLatency;
            arg.defaultLowOutputLatency = hit.Info.defaultLowOutputLatency;
            arg.defaultHighInputLatency = hit.Info.defaultHighInputLatency;
            arg.defaultHighOutputLatency = hit.Info.defaultHighOutputLatency;
            return true;
        }

        /// <summary>
        /// Find the live enumeration entry for a saved device, so the picker can
        /// pre-select it. Null when the saved device is not present. Same match
        /// rules as <see cref="FindDevice"/>.
        /// </summary>
        public static DeviceInfo FindLive(Device arg)
        {
            if (arg == null) return null;
            var theList = (arg.Type == DeviceTypes.input) ? InputDevices : OutputDevices;

            if (arg.hostApiTypeId >= 0)
            {
                DeviceInfo hit = BestMatch(arg, theList, requireApi: true);
                if (hit != null) return hit;
            }

            return BestMatch(arg, theList, requireApi: false);
        }

        /// <summary>
        /// One pass of the match rules. With <paramref name="requireApi"/> the
        /// identity is name + host API type id; without it, the legacy name +
        /// exact channel counts rule for files written before the type id
        /// existed. Either way only stream-capable rows are candidates, and the
        /// saved DevinfoID acts purely as a tie-breaker among equal matches.
        /// </summary>
        private static DeviceInfo BestMatch(Device saved, IReadOnlyList<DeviceInfo> live, bool requireApi)
        {
            DeviceInfo first = null;
            for (int id = 0; id < live.Count; id++)
            {
                DeviceInfo d = live[id];
                if (saved.Name != d.Info.name) continue;
                if (!d.UsableForRadioAudio) continue;
                if (requireApi)
                {
                    if (d.HostApiTypeId != saved.hostApiTypeId) continue;
                }
                else
                {
                    if (saved.maxInputChannels != d.Info.maxInputChannels
                        || saved.maxOutputChannels != d.Info.maxOutputChannels) continue;
                }

                // The remembered index confirms which of several equal matches
                // was meant. It cannot create a match on its own.
                if (d.DeviceID == saved.DevinfoID) return d;
                if (first == null) first = d;
            }
            return first;
        }
    }
}
