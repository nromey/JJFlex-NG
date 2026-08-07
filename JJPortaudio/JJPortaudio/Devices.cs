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
            /// <summary>PortAudio started and found no usable stereo devices.</summary>
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
            /// What a screen reader should read for this row. The Windows
            /// default is called out in words rather than by position, because
            /// "first in the list" is not information you can hear.
            /// </summary>
            public string Display
            {
                get
                {
                    string apiPart = string.IsNullOrEmpty(HostApiName) ? "" : " (" + HostApiName + ")";
                    return (IsDefault ? "System default: " : "") + Name + apiPart;
                }
            }
        }

        /// <summary>Last successful input enumeration. Empty until Enumerate runs.</summary>
        public static IReadOnlyList<DeviceInfo> InputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>Last successful output enumeration. Empty until Enumerate runs.</summary>
        public static IReadOnlyList<DeviceInfo> OutputDevices { get; private set; } = new List<DeviceInfo>();

        /// <summary>
        /// True while JJ Flex only lists two-channel devices. Kept as a named
        /// constant so the UI can say so out loud instead of leaving the user
        /// to conclude that JJ Flex cannot see their headset.
        /// </summary>
        public const bool StereoOnly = true;

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
        /// <returns>the persisted device, or null when there is no default.</returns>
        public Device AdoptSystemDefault(DeviceTypes type)
        {
            var list = (type == DeviceTypes.input) ? InputDevices : OutputDevices;
            DeviceInfo pick = null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsDefault) { pick = list[i]; break; }
            }
            if (pick == null && list.Count > 0) pick = list[0];
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
        /// Only two-channel devices are listed (see <see cref="StereoOnly"/>).
        /// </remarks>
        public static EnumerationStatus Enumerate(out string message)
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

                    if (pinfo.maxInputChannels == 2)
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

                    if (pinfo.maxOutputChannels == 2)
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
                message = StereoOnly
                    ? "No stereo audio devices were detected. JJ Flex lists two-channel devices only, so a mono microphone will not appear here. Attach or enable a stereo input and output device and choose Refresh."
                    : "No audio devices were detected. Attach or enable an input and output audio device and choose Refresh.";
                Tracing.TraceLine("Devices.Enumerate: no usable devices", TraceLevel.Error);
                return EnumerationStatus.NoDevices;
            }

            Tracing.TraceLine("Devices.Enumerate: " + inputs.Count + " input, "
                + outputs.Count + " output", TraceLevel.Info);
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
        /// PortAudio indexes reorder whenever the device list changes shape — a
        /// headset moved to another USB port, an interface added or removed.
        /// Matching on the index would silently bind to whatever device slid
        /// into that slot, and the worst case there is transmitting from the
        /// wrong microphone. So the index is never the identity.
        ///
        /// Two passes, strict first:
        ///   1. name + channel counts + host API type id — an exact match, used
        ///      when the saved entry carries the type id (written since
        ///      2026-08-07).
        ///   2. name + channel counts — the pre-2026-08-07 rule. Also the
        ///      answer when a host API disappears from the system entirely.
        /// A device that matches by name is the device the user picked, so this
        /// rebinds silently. Only a genuine no-match returns false, and that is
        /// the one case the caller announces.
        /// </remarks>
        public static bool FindDevice(Device arg)
        {
            if (arg == null) return false;
            var theList = (arg.Type == DeviceTypes.input) ? InputDevices : OutputDevices;

            if (arg.hostApiTypeId >= 0)
            {
                for (int id = 0; id < theList.Count; id++)
                {
                    if (Matches(arg, theList[id]) && theList[id].HostApiTypeId == arg.hostApiTypeId)
                    {
                        arg.DevinfoID = theList[id].DeviceID;
                        arg.hostApi = theList[id].Info.hostApi;
                        return true;
                    }
                }
            }

            for (int id = 0; id < theList.Count; id++)
            {
                if (Matches(arg, theList[id]))
                {
                    arg.DevinfoID = theList[id].DeviceID;
                    arg.hostApi = theList[id].Info.hostApi;
                    return true;
                }
            }

            return false;
        }

        private static bool Matches(Device saved, DeviceInfo live)
        {
            return saved.Name == live.Info.name
                && saved.maxInputChannels == live.Info.maxInputChannels
                && saved.maxOutputChannels == live.Info.maxOutputChannels;
        }

        /// <summary>
        /// Find the live enumeration entry for a saved device, so the picker can
        /// pre-select it. Null when the saved device is not present.
        /// </summary>
        public static DeviceInfo FindLive(Device arg)
        {
            if (arg == null) return null;
            var theList = (arg.Type == DeviceTypes.input) ? InputDevices : OutputDevices;

            if (arg.hostApiTypeId >= 0)
            {
                for (int id = 0; id < theList.Count; id++)
                {
                    if (Matches(arg, theList[id]) && theList[id].HostApiTypeId == arg.hostApiTypeId)
                        return theList[id];
                }
            }

            for (int id = 0; id < theList.Count; id++)
            {
                if (Matches(arg, theList[id])) return theList[id];
            }

            return null;
        }
    }
}
