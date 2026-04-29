using Flex.UiWpfFramework.Mvvm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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

        public delegate void HaapiFaultEventHandler(string noun, string reason);
        /// <summary>
        /// This event is raised when the client receives a fault message from the ha_api amplifier
        /// </summary>
        public event HaapiFaultEventHandler HaapiFault;
        private void OnHaapiFault(string noun, string reason)
        {
            if (HaapiFault is not null)
                HaapiFault(noun, reason);
        }

        public delegate void HaapiWarningEventHandler(string noun, string reason);
        /// <summary>
        /// This event is raised when the client receives a warning message from the ha_api amplifier
        /// </summary>
        public event HaapiWarningEventHandler HaapiWarning;
        private void OnHaapiWarning(string noun, string reason)
        {
            if (HaapiWarning is not null)
                HaapiWarning(noun, reason);
        }

        public delegate void HaapiWarningClearedEventHandler(string noun);
        /// <summary>
        /// This event is raised when a warning is cleared (state becomes OK) from the ha_api amplifier
        /// </summary>
        public event HaapiWarningClearedEventHandler HaapiWarningCleared;
        private void OnHaapiWarningCleared(string noun)
        {
            if (HaapiWarningCleared is not null)
                HaapiWarningCleared(noun);
        }

        public HAAPI(Radio radio)
        {
            _radio = radio;
            _meterList = new List<Meter>();
        }

        /// <summary>
        /// Sends a ha_api command to the radio using the _radio object
        /// </summary>
        /// <param name="haapi_cmd"> the ha_api command to send, DO NOT include the 
        /// "ha_api" command prefix as the function prepends that </param>
        public void SendHaapiCommand(string haapi_cmd)
        {
            string cmd = $"ha_api {haapi_cmd}";
            _radio.SendCommand(cmd);
        }

        private void HandleHaapiModeReply(int seq, uint resp_val, string s)
        {
            if (resp_val == 0)
            {
                // Command to change modes succeeded, RaisePropertyChanged to make _ampMode change visible
                RaisePropertyChanged(nameof(AmpMode));
            }

            else
            {
                // amplifier command mode did not succeed and we should default to standby and propogate that up to gui
                AmpMode = AmplifierMode.STANDBY;
            }
        }

        public void HaapiChangeMode(AmplifierMode mode)
        {
            _radio.SendReplyCommand(new ReplyHandler(HandleHaapiModeReply), $"ha_api amplifier set mode={mode.ToString().ToLower()}");

            // Save new mode but do not RaisePropertyChange until command succeeds
            _ampMode = mode;
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

                case "warning":
                    ParseWarningStatus(status.Substring("warning ".Length));
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
                            if (!float.TryParse(val, out float temp) || float.IsNaN(temp))
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
                            if (!float.TryParse(val, out float temp) || float.IsNaN(temp))
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
            if (string.IsNullOrEmpty(status))
            {
                Debug.WriteLine($"HAAPI::ParseFaultStatus: Empty fault status message");
                return;
            }

            // Parse key-value pairs from status string
            // Format: "type=detection source=combiner state=OK" or "type=detection source=combiner state=FAULTED"
            string[] kvs = status.Split(' ');
            string fault_type = null;
            string fault_source = null;
            string fault_state = null;

            foreach (string kv in kvs)
            {
                if (string.IsNullOrEmpty(kv)) continue;

                string[] parts = kv.Split('=');
                if (parts.Length != 2) continue;

                string key = parts[0].ToLower();
                string val = parts[1];

                switch (key)
                {
                    case "type":
                        fault_type = val;
                        break;
                    case "source":
                        fault_source = val;
                        break;
                    case "state":
                        fault_state = val;
                        break;
                }
            }

            // Only raise fault event when state is FAULTED
            if (string.IsNullOrEmpty(fault_state) || !fault_state.Equals("FAULTED", StringComparison.OrdinalIgnoreCase))
            {
                // State is OK or not FAULTED, so no fault to report
                return;
            }

            // Construct noun from type and source, or use source if type is not available
            string noun = string.IsNullOrEmpty(fault_source) 
                ? (string.IsNullOrEmpty(fault_type) ? "Unknown" : fault_type)
                : (string.IsNullOrEmpty(fault_type) ? fault_source : $"{fault_source} ({fault_type})");

            // Use state as the reason, or construct a descriptive message
            string reason = $"Fault detected - {fault_state}";
            if (!string.IsNullOrEmpty(fault_type) && !string.IsNullOrEmpty(fault_source))
            {
                reason = $"{fault_type} fault on {fault_source}";
            }
            else if (!string.IsNullOrEmpty(fault_type))
            {
                reason = $"{fault_type} fault";
            }
            else if (!string.IsNullOrEmpty(fault_source))
            {
                reason = $"Fault on {fault_source}";
            }

            OnHaapiFault(noun, reason);
        }

        private void ParseWarningStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                Debug.WriteLine($"HAAPI::ParseWarningStatus: Empty warning status message");
                return;
            }

            // Parse key-value pairs from status string
            // Format: "type=detection source=combiner state=OK" or "type=detection source=combiner state=WARNING"
            string[] kvs = status.Split(' ');
            string warning_type = null;
            string warning_source = null;
            string warning_state = null;

            foreach (string kv in kvs)
            {
                if (string.IsNullOrEmpty(kv)) continue;

                string[] parts = kv.Split('=');
                if (parts.Length != 2) continue;

                string key = parts[0].ToLower();
                string val = parts[1];

                switch (key)
                {
                    case "type":
                        warning_type = val;
                        break;
                    case "source":
                        warning_source = val;
                        break;
                    case "state":
                        warning_state = val;
                        break;
                }
            }

            // Construct noun from type and source, or use source if type is not available
            string noun = string.IsNullOrEmpty(warning_source) 
                ? (string.IsNullOrEmpty(warning_type) ? "Unknown" : warning_type)
                : (string.IsNullOrEmpty(warning_type) ? warning_source : $"{warning_source} ({warning_type})");

            // If state is OK, clear the warning
            if (!string.IsNullOrEmpty(warning_state) && warning_state.Equals("OK", StringComparison.OrdinalIgnoreCase))
            {
                OnHaapiWarningCleared(noun);
                return;
            }

            // Only raise warning event when state is WARNING
            if (string.IsNullOrEmpty(warning_state) || !warning_state.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                // State is not WARNING or OK, so no warning to report
                return;
            }

            // Use state as the reason, or construct a descriptive message
            string reason = $"Warning detected - {warning_state}";
            if (!string.IsNullOrEmpty(warning_type) && !string.IsNullOrEmpty(warning_source))
            {
                reason = $"{warning_type} warning on {warning_source}";
            }
            else if (!string.IsNullOrEmpty(warning_type))
            {
                reason = $"{warning_type} warning";
            }
            else if (!string.IsNullOrEmpty(warning_source))
            {
                reason = $"Warning on {warning_source}";
            }

            OnHaapiWarning(noun, reason);
        }

        #endregion

        #region Metering

        private List<Meter> _meterList;

        public Meter FindMeterByIndex(int index)
        {
            lock (_meterList)
                return _meterList.FirstOrDefault(m => m.Index == index);
        }

        public Meter FindMeterByName(string s)
        {
            lock (_meterList)
                return _meterList.FirstOrDefault(m => m.Name == s);
        }

        internal void AddMeter(Meter m)
        {
            lock (_meterList)
            {
                if (!_meterList.Contains(m))
                {
                    _meterList.Add(m);
                }
            }

            switch (m.Name.ToLower())
            {
                case "lpf_fwd_pwr":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiFwdPwr_DataReady);
                    break;
                case "lpf_swr":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiVswr_DataReady);
                    break;
                case "hv_sply_out_volt":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiHv_DataReady);
                    break;
                case "hv_sply_out_current":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiCurrent_DataReady);
                    break;
                case "hv_sply_temp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempPsu_DataReady);
                    break;
                case "pa_0_temp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempPa0_DataReady);
                    break;
                case "pa_1_temp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempPa1_DataReady);
                    break;
                case "drv_temp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempDrvA_DataReady);
                    break;
                case "comb_bal_load_temp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempComb_DataReady);
                    break;
                case "comb_hpf_load_tmp":
                    m.DataReady += new Meter.DataReadyEventHandler(HaapiTempHpf_DataReady);
                    break;
            }
        }

        internal void RemoveMeter(Meter m)
        {
            lock (_meterList)
            {
                if (_meterList.Contains(m))
                {
                    _meterList.Remove(m);

                    switch (m.Name.ToLower())
                    {
                        case "lpf_fwd_pwr":
                            m.DataReady -= HaapiFwdPwr_DataReady;
                            break;
                        case "lpf_swr":
                            m.DataReady -= HaapiVswr_DataReady;
                            break;
                        case "hv_sply_out_volt":
                            m.DataReady -= HaapiHv_DataReady;
                            break;
                        case "hv_sply_out_current":
                            m.DataReady -= HaapiCurrent_DataReady;
                            break;
                        case "hv_sply_temp":
                            m.DataReady -= HaapiTempPsu_DataReady;
                            break;
                        case "pa_0_temp":
                            m.DataReady -= HaapiTempPa0_DataReady;
                            break;
                        case "pa_1_temp":
                            m.DataReady -= HaapiTempPa1_DataReady;
                            break;
                        case "drv_temp":
                            m.DataReady -= HaapiTempDrvA_DataReady;
                            break;
                        case "comb_bal_load_temp":
                            m.DataReady -= HaapiTempComb_DataReady;
                            break;
                        case "comb_hpf_load_tmp":
                            m.DataReady -= HaapiTempHpf_DataReady;
                            break;
                    }
                }
            }
        }

        private void HaapiHv_DataReady(Meter meter, float data)
        {
            OnHaapiHVDataReady(data);
        }

        private void HaapiCurrent_DataReady(Meter meter, float data)
        {
            OnHaapiCurrentDataReady(data);
        }
        private void HaapiTempPsu_DataReady(Meter meter, float data)
        {
            OnHaapiTempPsuDataReady(data);
        }

        private void HaapiTempPa0_DataReady(Meter meter, float data)
        {
            OnHaapiTempPa0DataReady(data);
        }

        private void HaapiTempPa1_DataReady(Meter meter, float data)
        {
            OnHaapiTempPa1DataReady(data);
        }

        private void HaapiTempDrvA_DataReady(Meter meter, float data)
        {
            OnHaapiTempDrvADataReady(data);
        }
        private void HaapiTempDrvB_DataReady(Meter meter, float data)
        {
            OnHaapiTempDrvBDataReady(data);
        }

        private void HaapiTempComb_DataReady(Meter meter, float data)
        {
            OnHaapiTempCombDataReady(data);
        }

        private void HaapiTempHpf_DataReady(Meter meter, float data)
        {
            OnHaapiTempHpfDataReady(data);
        }

        private void HaapiFwdPwr_DataReady(Meter meter, float data)
        {
            OnHaapiFwdPwrDataReady(data);
        }

        private void HaapiVswr_DataReady(Meter meter, float data)
        {
            OnHaapiVswrDataReady(data);
        }

        /// <summary>
        /// Delegate used for HAAPI module metering inside of ssdr-client
        /// </summary>
        /// <param name="data"> The float containing formatted meter data </param>
        public delegate void MeterDataReadyEventHandler(float data);

        public event MeterDataReadyEventHandler HaapiFwdPwrDataReady;
        private void OnHaapiFwdPwrDataReady(float data)
        {
            if (HaapiFwdPwrDataReady != null)
                HaapiFwdPwrDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiVswrDataReady;
        private void OnHaapiVswrDataReady(float data)
        {
            if (HaapiVswrDataReady != null)
                HaapiVswrDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiHVDataReady;
        private void OnHaapiHVDataReady(float data)
        {
            if (HaapiHVDataReady != null)
                HaapiHVDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiCurrentDataReady;
        private void OnHaapiCurrentDataReady(float data)
        {
            if (HaapiCurrentDataReady != null)
                HaapiCurrentDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempPsuDataReady;
        private void OnHaapiTempPsuDataReady(float data)
        {
            if (HaapiTempPsuDataReady != null)
                HaapiTempPsuDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempPa0DataReady;
        private void OnHaapiTempPa0DataReady(float data)
        {
            if (HaapiTempPa0DataReady != null)
                HaapiTempPa0DataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempPa1DataReady;
        private void OnHaapiTempPa1DataReady(float data)
        {
            if (HaapiTempPa1DataReady != null)
                HaapiTempPa1DataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempDrvADataReady;
        private void OnHaapiTempDrvADataReady(float data)
        {
            if (HaapiTempDrvADataReady != null)
                HaapiTempDrvADataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempDrvBDataReady;
        private void OnHaapiTempDrvBDataReady(float data)
        {
            if (HaapiTempDrvBDataReady != null)
                HaapiTempDrvBDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempCombDataReady;
        private void OnHaapiTempCombDataReady(float data)
        {
            if (HaapiTempCombDataReady != null)
                HaapiTempCombDataReady(data);
        }

        public event MeterDataReadyEventHandler HaapiTempHpfDataReady;
        private void OnHaapiTempHpfDataReady(float data)
        {
            if (HaapiTempHpfDataReady != null)
                HaapiTempHpfDataReady(data);
        }

        #endregion

        #region TX Amplifier

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
