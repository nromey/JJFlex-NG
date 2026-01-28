using Flex.UiWpfFramework.Mvvm;
using Flex.Util;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace Flex.Smoothlake.FlexLib
{
    #region Enums
    public enum AmplifierMode
    {
        STANDBY,
        OPERATE
    }

    #endregion

    public class HAAPI : ObservableObject
    {
        private Radio _radio;
        private Slice _transmit_slice;

        public delegate void AmplifierFaultEventHandler(string reason);
        /// <summary>
        /// This event is raised when the client receives a ha_api amplifier fault message
        /// </summary>
        public event AmplifierFaultEventHandler AmplifierFault;
        private void OnAmplifierFault(string reason)
        {
            if (AmplifierFault is not null)
                AmplifierFault(reason);
        }

        public HAAPI(Radio radio)
        {
            _radio = radio;
            _radio.PropertyChanged += _radio_PropertyChanged;
        }

        private void _radio_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(_radio.TransmitSlice):
                    {
                        // Stop calling _transmit_slice_PropertyChanged for current _transmit_slice
                        if (_transmit_slice is not null)
                        {
                            _transmit_slice.PropertyChanged -= _transmit_slice_PropertyChanged;
                        }

                        _transmit_slice = _radio.TransmitSlice;

                        // Begin calling _transmit_slice_PropertyChanged for new _transmit_slice if not null
                        if (_transmit_slice is not null)
                        {
                            _transmit_slice.PropertyChanged += _transmit_slice_PropertyChanged;
                        }
                        break;
                    }
            }
        }

        private void _transmit_slice_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(_transmit_slice.TXAnt):
                    {
                        // This radio's TX ANT options should only be 'ANT1' and 'MOD' since it uses the high-power tx amp.
                        // If TXAnt is something different, we should log it and set AmpIsSelected to false
                        // Setting AmpIsSelected to false will force the antenna to switch back to ANT1 in the AmpIsSelected setter
                        if (_transmit_slice.TXAnt != "ANT1" && _transmit_slice.TXAnt != "MOD")
                        {
                            Debug.WriteLine($"HAAPI::_transmit_slice_PropertyChanged: Unsupported antenna port for this model radio {_transmit_slice.TXAnt}");
                            AmpIsSelected = false;
                            break;
                        }

                        AmpIsSelected = (_transmit_slice.TXAnt == "MOD");
                        break;
                    }
            }
        }

        #region Parse Routines

        public void ParseStatus(string status)
        {

            if (string.IsNullOrEmpty(status))
            {
                Debug.WriteLine($"HAAPI::ParseStatus: Empty HAAPI status message - nothing to parse");
                return;
            }

            switch (status.Split(' ')[0].ToLower())
            {
                case "amplifier":
                    ParseAmplifierStatus(status.Substring("amplifier ".Length));
                    break;

                case "bit":
                    break;

                case "combiner":
                    break;

                case "fault":
                    ParseFaultStatus(status.Substring("fault ".Length));
                    break;

                case "module":
                    break;

                case "pdu":
                    break;

                case "system":
                    break;
            }

        }

        private void ParseAmplifierStatus(string status)
        {
            string[] kvs = status.Split(' ');
            if (kvs.Length < 1)
            {
                Debug.WriteLine("HAAPI::ParseAmplifierStatus: No amplifier status to parse");
                return;
            }

            foreach (string kv in kvs)
            {
                string key = kv.Split('=')[0];
                string val = kv.Split('=')[1];

                switch (key.ToLower())
                {
                    case "frequency":
                        {
                            if (!float.TryParse(val, out float temp))
                            {
                                Debug.WriteLine($"HAAPI::ParseAmplifierStatus: Invalid frequency value {temp}");
                                continue;
                            }

                            _ampFrequency = temp;
                            RaisePropertyChanged(nameof(AmpFrequency));
                            break;
                        }

                    case "module_gain":
                        {
                            if (!float.TryParse(val, out float temp))
                            {
                                Debug.WriteLine($"HAAPI::ParseAmplifierStatus: Invalid module gain value {temp}");
                                continue;
                            }

                            _ampModuleGain = temp;
                            RaisePropertyChanged(nameof(AmpModuleGain));
                            break;
                        }

                    case "xmit_state":
                        {
                            if (!uint.TryParse(val, out uint temp))
                            {
                                Debug.WriteLine($"HAAPI::ParseAmplifierStatus: Invalid xmit_state {temp}");
                                continue;
                            }

                            _ampXmitState = Convert.ToBoolean(temp);
                            RaisePropertyChanged(nameof(AmpXmitState));
                            break;
                        }

                    case "mode":
                        {
                            if (val == "standby") _ampMode = AmplifierMode.STANDBY;

                            else if (val == "operate") _ampMode = AmplifierMode.OPERATE;

                            else
                            {
                                Debug.WriteLine($"HAAPI::ParseAmplifierStatus: Invalid amplifier mode {val}");
                                _ampMode = AmplifierMode.STANDBY;
                            }

                            RaisePropertyChanged(nameof(AmpMode));
                            break;
                        }
                }
            }
        }

        private void ParseFaultStatus(string status)
        {
            string noun_kv = status?.Split(' ')[0]; // eg. 'amplifier=1'
            string noun_key = noun_kv?.Split('=')[0];
            string noun_val = noun_kv?.Split('=')[1];

            if (string.IsNullOrEmpty(noun_kv) || string.IsNullOrEmpty(noun_key))
            {
                Debug.WriteLine($"HAAPI::ParseFaultStatus: Invalid noun field in fault message");
                return;
            }

            if (!uint.TryParse(noun_val, out uint faulted) || faulted != 1)
            {
                Debug.WriteLine($"HAAPI::ParseFaultStatus: Nonexistent or invalid fault for specified noun");
                return;
            }

            //extract reason from status update. If no reason is given, default to "Unknown fault"
            int reason_idx = status.IndexOf("reason=", StringComparison.OrdinalIgnoreCase);
            string fault_reason = (reason_idx < 0)? "Unknown fault" : status.Substring("reason=".Length + reason_idx);

            if (string.IsNullOrEmpty(fault_reason))
            {
                Debug.WriteLine("HAAPI::ParseFaultStatus: No reason value given for 'reason' key");
                fault_reason = "Unknown fault";
            }

            switch (noun_key.ToLower())
            {
                case "amplifier":
                    OnAmplifierFault(fault_reason);
                    break;
            }
        }

        #endregion

        #region TX Amplifier

        private bool _ampIsSelected;
        public bool AmpIsSelected
        {
            get => _ampIsSelected;
            set
            {
                if (value == _ampIsSelected) return;

                if (_radio?.TransmitSlice?.TXAnt is null)
                {
                    _ampIsSelected = false;
                    return;
                }

                _ampIsSelected = value;
                _radio.TransmitSlice.TXAnt = _ampIsSelected ? "MOD" : "ANT1";
                RaisePropertyChanged(nameof(AmpIsSelected));
            }
        }

        private float _ampFrequency;
        public float AmpFrequency
        {
            get => _ampFrequency;
            set
            {
                if (value == _ampFrequency) return;
                _ampFrequency = value;
                RaisePropertyChanged(nameof(AmpFrequency));

            }
        }

        private float _ampModuleGain;
        public float AmpModuleGain
        {
            get => _ampModuleGain;
            set
            {
                if (value == _ampModuleGain) return;
                _ampModuleGain = value;
                RaisePropertyChanged(nameof(AmpModuleGain));
            }
        }

        private bool _ampXmitState;
        public bool AmpXmitState
        {
            get => _ampXmitState;
            set
            {
                if (value == _ampXmitState) return;
                _ampXmitState = value;
                RaisePropertyChanged(nameof(AmpXmitState));
            }
        }

        private AmplifierMode _ampMode = AmplifierMode.STANDBY;
        public AmplifierMode AmpMode
        {
            get => _ampMode;
            set
            {
                if (value == _ampMode) return;
                _ampMode = value;
                RaisePropertyChanged(nameof(AmpMode));
            }
        }

        #endregion
    }
}
