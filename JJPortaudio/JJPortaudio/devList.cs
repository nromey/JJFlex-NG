//#define fullInfo
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using PortAudioSharp;

namespace JJPortaudio
{
    /// <summary>
    /// Device listing form.
    /// </summary>
    internal partial class devList : Form
    {
        private const string inputHeader = "Select input device";
        private const string outputHeader = "Select output device";
        private const string numdevsErr = "number devices error";
        private const string err = "Error";
        private const string mustSelect = "You must select a device";

        /// <summary>
        /// Hold the Portaudio device info.
        /// Note this is a struct.
        /// </summary>
        internal class infoType
        {
            public PortAudio.PaDeviceInfo Info;
            public Devices.DeviceTypes Type;
            public bool IsDefault;
            public int DeviceID; // index into the system's device array.
            public bool CanInput { get { return (Info.maxInputChannels > 0); } }
            public bool CanOutput { get { return (Info.maxOutputChannels > 0); } }
            // How to display the info.
            public string Display
            {
                get
                {
                    string str = (IsDefault) ? "Default: " : "";
#if fullInfo
                    str += Info.ToString();
#else
                    str += Info.name;
#endif
                    return str;
                }
            }
        }
        internal static List<infoType> InputDeviceList;
        internal static List<infoType> OutputDeviceList;

        /// <summary>
        /// Selected device if DialogResult.OK.
        /// </summary>
        public infoType SelectedDevice;

        /// <summary>
        /// Device type shown.
        /// </summary>
        public Devices.DeviceTypes DeviceType;

        private List<infoType> usedList;

        /// <summary>
        /// List audio devices
        /// </summary>
        /// <param name="type">input or output</param>
        public devList(Devices.DeviceTypes type)
        {
            InitializeComponent();
            DeviceType = type;
        }

        private void devList_Load(object sender, EventArgs e)
        {
            // Setup the list to display.
            DevListBox.DisplayMember = "Display";
            DevListBox.ValueMember = "Info";
            if ((DeviceType & Devices.DeviceTypes.input) != 0)
            {
                this.Text = inputHeader;
                usedList = InputDeviceList;
            }
            else
            {
                this.Text = outputHeader;
                usedList = OutputDeviceList;
            }
            DevListBox.DataSource = usedList;

            DialogResult = DialogResult.None;
        }

        private void SelectButton_Click(object sender, EventArgs e)
        {
            int id = DevListBox.SelectedIndex;
            if (id == -1)
            {
                MessageBox.Show(mustSelect, err, MessageBoxButtons.OK);
                return;
            }
            SelectedDevice = usedList[id];
            DialogResult = DialogResult.OK;
        }

        private void CnclButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
        }

        private void devList_FormClosing(object sender, FormClosingEventArgs e)
        {
        }

        /// <summary>
        /// Setup device lists.
        /// </summary>
        /// <returns>true on success</returns>
        /// <remarks>
        /// Note that we only handle stereo devices at present.
        /// </remarks>
        internal static bool Setup()
        {
            bool rv = false;
            InputDeviceList = new List<infoType>();
            OutputDeviceList = new List<infoType>();
            PortAudio.PaError PErr;
            if ((PErr = PortAudio.Pa_Initialize()) != 0)
            {
                MessageBox.Show(PortAudio.Pa_GetErrorText(PErr), "Init error", MessageBoxButtons.OK);
                return false;
            }

            // Get total # audio devices.
            int numDevs = PortAudio.Pa_GetDeviceCount();
            if (numDevs < 0)
            {
                PErr = (PortAudio.PaError)numDevs;
                MessageBox.Show(PortAudio.Pa_GetErrorText(PErr), numdevsErr, MessageBoxButtons.OK);
                goto done;
            }

            // Get default devices, input then output. Some systems return -1 (paNoDevice) when
            // no default exists; skip in that case instead of dereferencing a null pointer.
            infoType info = new infoType();
            int defaultInputId = PortAudio.Pa_GetDefaultInputDevice();
            if (defaultInputId >= 0)
            {
                info.Info = PortAudio.Pa_GetDeviceInfo(defaultInputId);
                if (info.Info.maxInputChannels == 2 && !string.IsNullOrEmpty(info.Info.name))
                {
                    info.IsDefault = true;
                    info.Type = Devices.DeviceTypes.input;
                    //info.DeviceID is set later.
                    InputDeviceList.Add(info);
                }
            }

            info = new infoType();
            int defaultOutputId = PortAudio.Pa_GetDefaultOutputDevice();
            if (defaultOutputId >= 0)
            {
                info.Info = PortAudio.Pa_GetDeviceInfo(defaultOutputId);
                if (info.Info.maxOutputChannels == 2 && !string.IsNullOrEmpty(info.Info.name))
                {
                    info.IsDefault = true;
                    info.Type = Devices.DeviceTypes.output;
                    //info.DeviceID is set later.
                    OutputDeviceList.Add(info);
                }
            }

            for (int i = 0; i < numDevs; i++)
            {
                info = new infoType();
                // Get the system's device info.
                info.Info = PortAudio.Pa_GetDeviceInfo(i);
                if (string.IsNullOrEmpty(info.Info.name))
                {
                    continue;
                }
                info.DeviceID = i;
                // must be a stereo input device.
                if (info.CanInput && (info.Info.maxInputChannels == 2))
                {
                    // Don't re-add the default.
                    if (!((InputDeviceList.Count > 0) && InputDeviceList[0].IsDefault &&
                         InputDeviceList[0].Info.Equals(info.Info)))
                    {
                        info.Type = Devices.DeviceTypes.input;
                        InputDeviceList.Add(info);
                    }
                    else
                    {
                        // it's the default, set the DeviceID.
                        InputDeviceList[0].DeviceID = i;
                    }
                }
                // Must be a stereo output device.
                if (info.CanOutput && (info.Info.maxOutputChannels == 2))
                {
                    if (!((OutputDeviceList.Count > 0) && OutputDeviceList[0].IsDefault &&
                         OutputDeviceList[0].Info.Equals(info.Info)))
                    {
                        info.Type = Devices.DeviceTypes.output;
                        OutputDeviceList.Add(info);
                    }
                    else
                    {
                        // it's the default, set the DeviceID.
                        OutputDeviceList[0].DeviceID = i;
                    }
                }
            }

            rv = ((InputDeviceList.Count > 0) || (OutputDeviceList.Count > 0));
            if (!rv)
            {
                MessageBox.Show("No audio devices were detected by PortAudio. Please attach/enable an input and output audio device and try again.", err, MessageBoxButtons.OK);
            }

            done:
            PortAudio.Pa_Terminate();
            return rv;
        }

        /// <summary>
        /// See if the device in arg matches a system device.
        /// </summary>
        /// <param name="arg">Device to match, may be null.</param>
        /// <returns>true if found; arg.DevinfoID is set.</returns>
        internal static bool FindDevice(Devices.Device arg)
        {
            if (arg == null) return false;
            List<infoType> theList = (arg.Type == Devices.DeviceTypes.input) ? InputDeviceList : OutputDeviceList;
            for (int id = 0; id < theList.Count; id++)
            {
                if ((arg.Name == theList[id].Info.name) &
                    (arg.maxInputChannels == theList[id].Info.maxInputChannels) &
                    (arg.maxOutputChannels == theList[id].Info.maxOutputChannels))
                {
                    arg.DevinfoID = theList[id].DeviceID;
                    return true;
                }
            }
            return false;
        }
    }
}
