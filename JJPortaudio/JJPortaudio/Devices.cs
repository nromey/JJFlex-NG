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

            // --- Added 2026-08-12 (Mic Track), device-picker grouping ---------
            // One physical device shows up once per host API — a USB interface
            // typically appears three times (MME, DirectSound, WASAPI) plus its
            // kernel pins. Listing every one of them as its own choice is how an
            // operator ends up picking a dead endpoint. Rows are grouped by
            // physical device; one row per group is shown, and the rest hang off
            // it here so nothing is lost and the advanced view can show them all.

            /// <summary>
            /// The row that represents this physical device in the picker. Every
            /// member of a group points at the same representative, and the
            /// representative points at itself. Never null after
            /// <see cref="Enumerate"/>.
            /// </summary>
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
            /// Set on a group's representative when ANY endpoint of that
            /// physical device is the Windows default. Kept separate from
            /// <see cref="IsDefault"/>, which stays true of exactly the one
            /// endpoint PortAudio flagged: PortAudio's default input on the
            /// development machine was the MME endpoint, so a collapsed list
            /// showing the WASAPI row would otherwise stop calling the default
            /// out by name — and copying the flag onto the representative would
            /// make the advanced list claim two system defaults.
            /// </summary>
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
            /// True when the radio-audio engine can open this device today:
            /// it has at least the <see cref="StreamChannels"/> the stereo
            /// stream needs. Devices with MORE channels qualify — PortAudio's
            /// contract allows opening any channel count from 1 up to the
            /// device's maximum, so the engine's two-channel open is the
            /// downmix. Mono devices are still LISTED (hiding a device for
            /// its channel count is how a laptop's only real microphone
            /// became unselectable) but cannot carry the stream until the
            /// engine can open mono and upmix.
            /// </summary>
            public bool UsableForRadioAudio => NativeChannels >= StreamChannels;

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
            /// fact. Once duplicate endpoints are collapsed (see
            /// <see cref="PickerInputDevices"/>) the API is plumbing, not a
            /// choice, and reading "Windows WASAPI" after every single device
            /// is noise on every arrow press. It comes back in the advanced
            /// view, where telling the endpoints apart is the entire point.
            /// </remarks>
            public string Display
            {
                get
                {
                    if (IsMissingSaved)
                        return "Not connected: " + Name + " — your saved choice, plug it back in or pick another";

                    // Collapsed view: the default belongs to the physical
                    // device. Advanced view: to the exact endpoint PortAudio
                    // flagged, because telling endpoints apart is the whole
                    // point of that view.
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

                    // Naming the API is useful exactly when more than one row
                    // for the same hardware is on screen.
                    if (ShowAdvancedDevices && !string.IsNullOrEmpty(HostApiName))
                        sb.Append(" (").Append(HostApiName).Append(')');

                    if (!UsableForRadioAudio) sb.Append(" — mono, not usable yet");
                    return sb.ToString();
                }
            }
        }

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
        /// True when a group's representative stays off the basic picker.
        /// Basic mode filters as well as folds (2026-08-13): folding got the
        /// list to one row per physical device, but on a real machine that
        /// still left one row for every loopback and virtual-cable endpoint —
        /// of eight folded inputs on the machine that prompted this, only two
        /// or three were things a person could talk into. Hidden, never
        /// removed: these rows stay enumerated, keep resolving saved
        /// selections, and come back with the advanced toggle — someone
        /// routing audio through a cable on purpose is exactly who turns that
        /// toggle on. Same argument that hid the WDM-KS kernel pins on
        /// 2026-08-11, one step further.
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
        /// Mic Track, 2026-08-12. A USB interface enumerates once per host API,
        /// so a single Focusrite arrives as three identical-looking choices
        /// (MME, DirectSound, WASAPI) and a multi-input interface multiplies
        /// that by its channel pairs — the development machine listed 26 input
        /// rows for what a person would call four devices. Choosing correctly
        /// out of that is guesswork, and guessing wrong means transmitting
        /// silence. The duplicates are not deleted, only folded: each surviving
        /// row carries the rest in <see cref="DeviceInfo.Alternates"/>, and
        /// <see cref="ShowAdvancedDevices"/> shows the unfolded list.
        ///
        /// Since 2026-08-13 the folded list is also FILTERED: WASAPI loopbacks
        /// and virtual audio cables are hidden until the advanced toggle is
        /// on, because folding alone still showed one row per physical thing
        /// Windows exposes and most of those are not things anyone can talk
        /// into. See <see cref="HiddenFromBasicPicker"/>.
        /// </remarks>
        public static IReadOnlyList<DeviceInfo> PickerInputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// The output list a person should be asked to choose from: one row per
        /// physical device. See <see cref="PickerInputDevices"/>.
        /// </summary>
        public static IReadOnlyList<DeviceInfo> PickerOutputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// Channel count of the radio-audio stream itself: the engine
        /// (Audio.cs) opens every PortAudio stream two-channel float because
        /// the Opus path is stereo. This is a property of the STREAM, never a
        /// device filter — the old <c>StereoOnly</c> constant stood beside a
        /// channels==2 list test that silently dropped every 4-channel device,
        /// including a laptop's only real microphone array. Devices now list
        /// by capability: two or more channels open as stereo (in PortAudio's
        /// contract, which permits opening 1..max channels), and mono devices
        /// are listed but flagged, because PortAudio rejects a two-channel
        /// open on a one-channel device and the open call lives in the engine,
        /// not here. See <see cref="DeviceInfo.UsableForRadioAudio"/>.
        /// </summary>
        public const int StreamChannels = 2;

        /// <summary>PortAudio PaHostApiTypeId for WDM-KS (kernel streaming). Fixed
        /// enum value. Used to hide kernel pins from the picker by default.</summary>
        public const int WdmKsTypeId = 11;

        /// <summary>When true, the enumeration includes WDM-KS kernel pins (for
        /// power users via an "advanced devices" toggle). Default false — those
        /// pins are the trap that had operators transmitting into dead jacks.</summary>
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
            writeCFG();
            return configured.devs[id];
        }

        /// <summary>
        /// Adopt the system default for this type, saving it like any other
        /// choice. Used by the fallback path when a saved device has vanished:
        /// the operator gets audio, and the settings surface can show what it
        /// actually fell back to.
        /// </summary>
        /// <returns>the persisted device, or null when nothing usable exists.</returns>
        /// <remarks>
        /// Only devices the engine can actually open are candidates. If the
        /// Windows default is a mono device, adopting it would hand the engine
        /// a stream open that PortAudio must reject — dead audio wearing a
        /// saved configuration. Falling to the first stream-capable device, or
        /// to null (which the caller announces), keeps the failure audible.
        /// </remarks>
        public Device AdoptSystemDefault(DeviceTypes type)
        {
            var list = (type == DeviceTypes.input) ? InputDevices : OutputDevices;
            DeviceInfo pick = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].UsableForRadioAudio) continue;
                if (pick == null) pick = list[i];
                if (list[i].IsDefault) { pick = list[i]; break; }
            }
            if (pick == null) return null;
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

                    // Log every endpoint by name/API/channels — the one line that
                    // lets us diagnose a bad device pick from a trace (a mono or
                    // multi-channel mic hidden by the stereo filter is otherwise
                    // invisible in every other log).
                    Tracing.TraceLine("Devices.Enumerate: dev " + i + ": \"" + pinfo.name + "\" api=" + apiName
                        + " in=" + pinfo.maxInputChannels + " out=" + pinfo.maxOutputChannels
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
                    // open IS the downmix. Mono devices cannot satisfy that
                    // open (PortAudio validates channelCount against the
                    // device maximum), so they carry UsableForRadioAudio=false
                    // and every surface says so instead of hiding them.
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

            PickerInputDevices = BuildPickerList(inputs);
            PickerOutputDevices = BuildPickerList(outputs);

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
            Tracing.TraceLine("Devices.Enumerate: " + inputs.Count + " input ("
                + usableIn + " stereo-capable), " + outputs.Count + " output ("
                + usableOut + " stereo-capable)", TraceLevel.Info);
            return EnumerationStatus.Ok;
        }

        // ------------------------------------------------- picker grouping

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
                case 13: return 0;  // paWASAPI
                case 1:  return 1;  // paDirectSound
                case 2:  return 2;  // paMME
                case 11: return 4;  // paWDMKS
                default: return 3;
            }
        }

        /// <summary>
        /// Fold every endpoint of one physical device into a single row,
        /// leave out the rows nobody can talk into (basic mode only — see
        /// <see cref="HiddenFromBasicPicker"/>), and return the rows a person
        /// should choose from.
        /// </summary>
        /// <remarks>
        /// Grouping is by device name, normalised for case and whitespace, plus
        /// one extra rule for MME truncation (see <see cref="MmeNameLimit"/>): a
        /// short name that is a prefix of exactly ONE longer name is the same
        /// device seen through MME. "Exactly one" matters — where a prefix
        /// matches several longer names the answer is genuinely ambiguous, and
        /// merging on a guess would hide a real device behind an unrelated one,
        /// so an ambiguous prefix stays its own row.
        ///
        /// Channel counts are NOT part of the key. The same interface reports
        /// different channel counts under different host APIs, and one physical
        /// device is one choice regardless.
        /// </remarks>
        private static List<DeviceInfo> BuildPickerList(List<DeviceInfo> all)
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
                bool allMme = shortBucket.TrueForAll(d => d.HostApiTypeId == 2);
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
                        Tracing.TraceLine("Devices.BuildPickerList: \"" + shortBucket[0].Name
                            + "\" is a prefix of more than one device name; leaving it as its own row",
                            TraceLevel.Info);
                    }
                    continue;
                }

                byName[target].AddRange(shortBucket);
                shortBucket.Clear();
            }

            var picker = new List<DeviceInfo>();
            var hidden = new List<DeviceInfo>();
            foreach (string key in order)
            {
                List<DeviceInfo> bucket = byName[key];
                if (bucket.Count == 0) continue;   // folded into another group

                DeviceInfo owner = ChooseRepresentative(bucket);
                var alternates = new List<DeviceInfo>();
                foreach (DeviceInfo d in bucket)
                {
                    d.GroupOwner = owner;
                    if (!ReferenceEquals(d, owner)) alternates.Add(d);
                }
                owner.Alternates = alternates;

                // The Windows default belongs to the physical device, not to
                // the endpoint that happened to carry the flag.
                owner.GroupIsSystemDefault = false;
                foreach (DeviceInfo d in bucket)
                {
                    if (d.IsDefault) { owner.GroupIsSystemDefault = true; break; }
                }

                // Basic mode hides what nobody can talk into — see
                // HiddenFromBasicPicker for the reasoning. This runs AFTER the
                // group is fully wired, so a saved selection that resolves to
                // a hidden row still resolves: the row is off the menu, not
                // gone. The trace line is not optional decoration — every
                // silently-hidden-device bug this file has ever had was
                // diagnosed from these lines, or dragged on because one was
                // missing.
                if (!ShowAdvancedDevices && HiddenFromBasicPicker(owner))
                {
                    Tracing.TraceLine("Devices.BuildPickerList: basic mode hides \"" + owner.Name
                        + "\" (" + (IsLoopbackName(owner.Name) ? "loopback" : "virtual cable") + ")",
                        TraceLevel.Info);
                    hidden.Add(owner);
                    continue;
                }

                picker.Add(owner);
            }

            // If the filter is the only reason the list is empty, the filter
            // loses. On a machine whose inputs really are all virtual cables
            // (streaming rigs, VMs), the cable is the closest thing to a
            // microphone there is, and an empty picker while devices enumerate
            // would read as "no audio devices" — a lie, and the exact kind of
            // silent disappearance this file exists to never produce.
            if (picker.Count == 0 && hidden.Count > 0)
            {
                Tracing.TraceLine("Devices.BuildPickerList: the basic-mode filter hid every device; "
                    + "showing all " + hidden.Count + " rather than an empty list", TraceLevel.Info);
                picker.AddRange(hidden);
                hidden.Clear();
            }

            if (ShowAdvancedDevices)
            {
                // Advanced view: every endpoint, still grouped (so a saved
                // device resolves the same way), just nothing hidden.
                Tracing.TraceLine("Devices.BuildPickerList: advanced view, showing all "
                    + all.Count + " endpoints", TraceLevel.Info);
                return new List<DeviceInfo>(all);
            }

            Tracing.TraceLine("Devices.BuildPickerList: " + all.Count + " endpoints folded into "
                + picker.Count + " shown device(s)"
                + (hidden.Count > 0 ? " plus " + hidden.Count + " hidden (loopback/virtual cable)" : ""),
                TraceLevel.Info);
            return picker;
        }

        /// <summary>
        /// Pick the endpoint that speaks for a physical device: best host API
        /// first, then the one the engine can actually open, then the Windows
        /// default. A representative the engine cannot open would be a row that
        /// refuses every time it is chosen.
        /// </summary>
        private static DeviceInfo ChooseRepresentative(List<DeviceInfo> bucket)
        {
            DeviceInfo best = null;
            foreach (DeviceInfo d in bucket)
            {
                if (best == null) { best = d; continue; }

                bool dUsable = d.UsableForRadioAudio;
                bool bestUsable = best.UsableForRadioAudio;
                if (dUsable != bestUsable)
                {
                    if (dUsable) best = d;
                    continue;
                }

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
        /// The picker row that stands for a saved device: its group's
        /// representative when the saved endpoint is present, otherwise null.
        /// </summary>
        /// <remarks>
        /// A saved MME endpoint and the WASAPI row now shown for the same
        /// hardware are the same microphone to the person who chose it, so the
        /// picker must land on that row rather than announcing that their
        /// device has gone missing.
        /// </remarks>
        public static DeviceInfo FindPickerRow(Device saved)
        {
            DeviceInfo live = FindLive(saved);
            if (live == null) return null;
            // The advanced view lists endpoints, so it must land on the exact
            // endpoint that was saved — that is the fact someone opened that
            // view to see.
            if (ShowAdvancedDevices) return live;
            return live.GroupOwner ?? live;
        }

        /// <summary>
        /// True when <paramref name="row"/> represents the same physical device
        /// as <paramref name="saved"/> — so a picker OK can leave a working
        /// configuration alone instead of rewriting it to a different endpoint
        /// of the same hardware.
        /// </summary>
        public static bool SameDevice(Device saved, DeviceInfo row)
        {
            if (saved == null || row == null || row.IsMissingSaved) return false;

            DeviceInfo savedLive = FindLive(saved);
            if (savedLive == null) return false;

            // In the advanced view the operator is choosing an ENDPOINT on
            // purpose — that is the only reason to be in that view — so
            // "unchanged" there means the very same endpoint. In the collapsed
            // view they are choosing a piece of hardware, and any endpoint of
            // it means their configuration is already right.
            if (ShowAdvancedDevices) return ReferenceEquals(savedLive, row);

            DeviceInfo savedOwner = savedLive.GroupOwner ?? savedLive;
            DeviceInfo rowOwner = row.GroupOwner ?? row;
            return ReferenceEquals(savedOwner, rowOwner);
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
