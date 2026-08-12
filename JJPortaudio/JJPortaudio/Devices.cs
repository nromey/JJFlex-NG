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

            public string Name => Info.name ?? "";
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
            /// What a screen reader should read for this row. The Windows
            /// default is called out in words rather than by position, because
            /// "first in the list" is not information you can hear. A mono
            /// device says so — the row itself carries the reason it cannot
            /// be chosen, instead of a silent refusal later.
            /// </summary>
            public string Display
            {
                get
                {
                    string apiPart = string.IsNullOrEmpty(HostApiName) ? "" : " (" + HostApiName + ")";
                    return (IsDefault ? "System default: " : "") + Name + apiPart
                        + (UsableForRadioAudio ? "" : " — mono, not usable yet");
                }
            }
        }

        /// <summary>Last successful input enumeration. Empty until Enumerate runs.</summary>
        public static IReadOnlyList<DeviceInfo> InputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>Last successful output enumeration. Empty until Enumerate runs.</summary>
        public static IReadOnlyList<DeviceInfo> OutputDevices { get; private set; } = new List<DeviceInfo>();

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
