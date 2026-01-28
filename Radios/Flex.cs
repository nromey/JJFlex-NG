#define CWMonitor
//#define opusToFile
#define ClearWebCache
#define FlexGroups
#define TXAudioTest
//#define GetMenuz
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Flex superclass
    /// </summary>
    public class Flex : AllRadios
    {
        private const string statusHdr = "Status";
        private const string importedMsg = "Import complete";
        private const string importFailMsg = "import didn't complete";

        private static OpusStream myOpusStream = null;
        private static bool useOpus { get { return (myOpusStream != null); } }
        private static void opusStreamAddedHandler(OpusStream stream)
        {
            Tracing.TraceLine("opusStreamAddedHandler", TraceLevel.Info);
            myOpusStream = stream;
        }

        // Currently we only handle one radio.
        internal Radio theRadio;
        private static bool _apiSetup = false;
        private static void apiSetup()
        {
            Tracing.TraceLine("apiSetup", TraceLevel.Info);
            if (_apiSetup)
            {
                API.CloseSession();
                API.RadioAdded -= radioFound;
                API.RadioRemoved -= radioRemoved;
                Tracing.TraceLine("api was setup", TraceLevel.Info);
            }

            _apiSetup = true;
            Radios = new List<Radio>();
            API.RadioAdded += radioFound;
            API.RadioRemoved += radioRemoved;
            API.ProgramName = "JJRadio";
            API.IsGUI = true;
            API.Init();
        }

        protected static List<Radio> Radios = null;
        /// <summary>
        /// Raises the RadioDiscoveredEventArgs interrupt.
        /// </summary>
        /// <param name="radio">the radio</param>
        private static void radioFound(Radio radio)
        {
#if zero
            // See if an older version of this exists.
            foreach (Radio r in Radios)
            {
                if (radio.Serial == r.Serial)
                {
                    Radios.Remove(r);
                    break;
                }
            }
#endif

            Radios.Add(radio);

            RadioDiscoveredEventArgs arg = new RadioDiscoveredEventArgs(radio.Nickname, radio.Model, radio.Serial);
            Tracing.TraceLine("radioFound:" + arg.Name + " " + arg.Model, TraceLevel.Info);
            RaiseRadioDiscovered(arg);
        }

        private static void radioRemoved(Radio radio)
        {
        }

        /// <summary>
        /// Discover radios.
        /// </summary>
        /// <remarks>
        /// First allow localPeriod of time to discover radios on the local LAN.
        /// Then check the WAN.
        /// </remarks>
        internal static Radio DiscoverFlexRadios(RadioDiscoveredEventArgs arg, bool allowRemote)
        {
            Tracing.TraceLine("DiscoverFlexRadios:" + (string)((arg != null) ? arg.Serial : ""), TraceLevel.Info);
            Radio rv = null;
            apiSetup(); // Discovers local radios.
            Thread.Sleep(1000); // wait a second
            if (arg == null) return null; // data returned via interrupt (LAN only), see radioFound().

            // Called locally.  Return radio on LAN if found, otherwise check the WAN.
            await(() => { return ((rv = returnFlexRadio(arg)) != null); }, 1000);
            if (allowRemote & (rv == null))
            {
                // Check the WAN, no local radios.
                rv = setupRemote(arg);
            }
            return rv;
        }

        /// <summary>
        /// Return the Flex radio matching the argument
        /// </summary>
        /// <param name="arg">RadioDiscoveredEventArgs value</param>
        /// <returns>a Radio or null.</returns>
        internal static Radio returnFlexRadio(RadioDiscoveredEventArgs arg)
        {
            Radio rv = null;
            if ((Radios != null) && (Radios.Count > 0))
            {
                foreach (Radio r in Radios)
                {
                    if (r.Serial == arg.Serial)
                    {
                        rv = r;
                        //break;
                    }
                }
            }
            return rv;
        }

        /// <summary>
        /// Manually provide flex remote info.
        /// </summary>
        /// <param name="existingInfo">(optional) existing info</param>
        internal static RadioDiscoveredEventArgs FlexManualNetworkRadioInfo(AllRadios.RadioDiscoveredEventArgs existingInfo)
        {
            RadioDiscoveredEventArgs rv = existingInfo;
            UserEnteredRemoteRigInfo infoDialog = new UserEnteredRemoteRigInfo(existingInfo);
            Form theForm = (Form)infoDialog;
            if (theForm.ShowDialog() == DialogResult.OK)
            {
                rv = infoDialog.Arg;
            }
            theForm.Dispose();
            return rv;
        }

        // region - WAN
        #region WAN
        private static bool wanListReceived;
        private static void wanRadioListReceivedHandler(List<Radio> lst)
        {
            Tracing.TraceLine("wanRadioListReceivedHandler:" + lst.Count, TraceLevel.Info);
            foreach (Radio r in lst)
            {
                radioFound(r);
            }
            wanListReceived = true;
        }

        private static WanServer wan = null;
        private static string wanConnectionHandle;
        private static bool WanRadioConnectReadyReceived = false;
        private static void WanRadioConnectReadyHandler(string handle, string serial)
        {
            wanConnectionHandle = handle;
            WanRadioConnectReadyReceived = true;
        }

        private static string[] tokens;
        private static Radio setupRemote(RadioDiscoveredEventArgs arg)
        {
            Radio rv = null;
            Tracing.TraceLine("setupRemote:" + (string)((arg != null) ? arg.Serial : ""), TraceLevel.Info);
#if ClearWebCache
            WebBrowserHelper.ClearCache();
#endif

            // Bringup auth form.  Must be in an sta thread.
            tokens = null;
            Thread authThread = new Thread(authFormProc);
            authThread.Name = "authThread";
            authThread.SetApartmentState(ApartmentState.STA);
            authThread.Start();
            while (authThread.IsAlive) { Thread.Sleep(100); }
            if ((tokens == null) || (tokens.Length == 0))
            {
                Tracing.TraceLine("setup Remote: no tokens returned from form", TraceLevel.Error);
                goto setupRemoteDone;
            }

            // Get the jwt.
            string jwt = null;
            foreach (string keyVal in tokens)
            {
                string[] vals = keyVal.Split(new char[] { '=' });
                if (vals[0] == "id_token")
                {
                    jwt = vals[1];
                    break;
                }
            }
            if (jwt == null)
            {
                Tracing.TraceLine("setupRemote: no jwt", TraceLevel.Error);
                goto setupRemoteDone;
            }

            try
            {
                if (wan != null)
                {
                    Tracing.TraceLine("setupRemote:wan was setup, disconnecting", TraceLevel.Info);
                    wan.Disconnect();
                    Thread.Sleep(1000);
                }

                wan = new WanServer();
                wan.WanRadioConnectReady += new WanServer.WanRadioConnectReadyEventHandler(WanRadioConnectReadyHandler);

                wan.Connect();
                if (!wan.IsConnected)
                {
                    Tracing.TraceLine("setupRemote: not connected!", TraceLevel.Error);
                    goto setupRemoteDone;
                }

                Tracing.TraceLine("setupRemote: SendRegisterApplicationMessageToServer: " + API.ProgramName + ' ' + "Win10" + ' ' + jwt, TraceLevel.Info);
                WanServer.WanRadioRadioListRecievedEventHandler evHand = new WanServer.WanRadioRadioListRecievedEventHandler(wanRadioListReceivedHandler);
                WanServer.WanRadioRadioListRecieved += evHand;
                wanListReceived = false;
                wan.SendRegisterApplicationMessageToServer(API.ProgramName, "Win10", jwt);
                if (!await(() => { return wanListReceived; }, 5000))
                {
                    Tracing.TraceLine("setupRemote: no radios found.", TraceLevel.Error);
                    goto setupRemoteDone;
                }
                WanServer.WanRadioRadioListRecieved -= evHand;

                rv = returnFlexRadio(arg);
                if (rv == null)
                {
                    Tracing.TraceLine("setupRemote: no matching radio", TraceLevel.Error);
                    goto setupRemoteDone;
                }

                Tracing.TraceLine("setupRemote SendConnectMessageToRadio: " + rv.Serial, TraceLevel.Info);
                WanRadioConnectReadyReceived = false;
                wanListReceived = false;
                wan.SendConnectMessageToRadio(rv.Serial, 0);
                if (!await(() => { return WanRadioConnectReadyReceived; }, 2000))
                {
                    Tracing.TraceLine("setupRemote Radio not ready for connect.", TraceLevel.Error);
                    rv = null;
                    goto setupRemoteDone;
                }
                rv.WANConnectionHandle = wanConnectionHandle;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("setupRemote: exception in setupThreadProc: " + ex.Message, TraceLevel.Error);
                rv = null;
            }

            setupRemoteDone:
            if ((rv == null) & (wan != null)) wan.Disconnect();
            return rv;
        }

        private static void authFormProc()
        {
            AuthForm form = new AuthForm();
            ((Form)form).ShowDialog();
            tokens = form.Tokens;
            form.Dispose();
        }
        #endregion

        internal delegate void FreqChangeDel(object o);
        /// <summary>
        /// Called when RX frequency changes.
        /// Also called to copy a panadapter/waterfall.
        /// </summary>
        internal FreqChangeDel RXFreqChange = null;

        internal delegate void UpdateConfiguredTNFsDel(TNF tnf);
        internal UpdateConfiguredTNFsDel UpdateConfiguredTNFs = null;

        private ATUTuneStatus originalATUStatus = ATUTuneStatus.None;
        private bool oldATUEnable = false; // false is the default, see Flex6300.
        private bool wasConnected = false;
        private bool JJRadioDefaultLoaded = false;
        internal bool JJRadioDefaultSelected = false;
        private void propertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            string line; // used for trace.
            if (sender is Radio)
            {
                Tracing.TraceLine("propertyChanged:Radio:" + e.PropertyName, TraceLevel.Verbose);
                Radio r = (Radio)sender;
                switch (e.PropertyName)
                {
                    case "ActiveSlice":
                        {
                            if (r.ActiveSlice != null)
                            {
                                Tracing.TraceLine("ActiveSlice:" + r.ActiveSlice.Index.ToString(), TraceLevel.Info);
                                _RXVFO = SliceToVFO(r.ActiveSlice);
                            }
                        }
                        break;
                    case "ATUEnabled":
                        {
                            Tracing.TraceLine("ATUEnabled:" + theRadio.ATUEnabled.ToString(), TraceLevel.Info);
                            if (oldATUEnable == r.ATUEnabled) return;
                            oldATUEnable = r.ATUEnabled;
                            bool wasEnabled = myCaps.HasCap(RigCaps.Caps.ATGet);
                            if (r.ATUEnabled)
                            {
                                // indicate ATU capable.
                                myCaps.getCaps = myCaps.SetCap(myCaps.getCaps, RigCaps.Caps.ATGet);
                                myCaps.setCaps = myCaps.SetCap(myCaps.setCaps, RigCaps.Caps.ATSet);
                                myCaps.getCaps = myCaps.SetCap(myCaps.getCaps, RigCaps.Caps.ATMems);
                                // Turn off the tuner if was bypassed.
                                // Note the bypass status might happen later.
                                if ((originalATUStatus == ATUTuneStatus.Bypass) |
                                    (originalATUStatus==ATUTuneStatus.ManualBypass))
                                {
                                    setFlexTunerTypeNotAuto();
                                }
                                else _FlexTunerType = FlexTunerTypes.auto;
                            }
                            else
                            {
                                // not atu capable.
                                myCaps.getCaps = myCaps.ResetCap(myCaps.getCaps, RigCaps.Caps.ATGet);
                                myCaps.setCaps = myCaps.ResetCap(myCaps.setCaps, RigCaps.Caps.ATSet);
                                myCaps.getCaps = myCaps.ResetCap(myCaps.getCaps, RigCaps.Caps.ATMems);
                                setFlexTunerTypeNotAuto();
                            }
                            if (wasEnabled != myCaps.HasCap(RigCaps.Caps.ATGet))
                            {
                                // enabled status changed.
                                RaiseCapsChange(new CapsChangeArg(myCaps));
                            }
                        }
                        break;
                    case "ATUTuneStatus":
                        {
                            Tracing.TraceLine("ATUTuneStatus:" + theRadio.ATUTuneStatus.ToString(), TraceLevel.Info);
                            // set original status
                            if (originalATUStatus == ATUTuneStatus.None) originalATUStatus = r.ATUTuneStatus;
                            RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                (_FlexTunerType, r.ATUTuneStatus, SWR));
                            switch (theRadio.ATUTuneStatus)
                            {
                                case ATUTuneStatus.NotStarted:
                                    // turn off tuning.
                                    FlexTunerOn = false;
                                    break;
                                case ATUTuneStatus.Aborted:
                                    // turn off tuning.
                                    FlexTunerOn = false;
                                    break;
                                case ATUTuneStatus.InProgress:
                                    // nothing to do here.
                                    break;
                                case ATUTuneStatus.Bypass:
                                    // stop tuning if tuning
                                    if (_FlexTunerOn) FlexTunerOn = false;
                                    // Turn off autoTune.
                                    setFlexTunerTypeNotAuto();
                                    break;
                                case ATUTuneStatus.ManualBypass:
                                    // Nothing to do
                                    break;
                                case ATUTuneStatus.Successful:
                                case ATUTuneStatus.OK:
                                    FlexTunerOn = false;
                                    break;
                                case ATUTuneStatus.Fail:
                                    FlexTunerOn = false;
                                    // bypass the tuner
                                    theRadio.ATUTuneBypass(); // will get manualBypass status
                                    // Turn autotune off
                                    setFlexTunerTypeNotAuto();
                                    break;
                                case ATUTuneStatus.FailBypass:
                                    // nothing to do
                                    break;
                            }
                        }
                        break;
                    case "Connected":
                        {
                            Tracing.TraceLine("Connected:" + r.Connected + ' ' + _Reconnect, TraceLevel.Error);
                            if (!r.Connected)
                            {
                                raisePowerOff();
                                if (wasConnected & _Reconnect)
                                {
                                    if (q != null)
                                    {
                                        q.Enqueue((FunctionDel)(() => { reconnect(); }));
                                    }
                                    else Tracing.TraceLine("property connect:no queue", TraceLevel.Error);
                                }
                            }
                            else
                            {
                                if (wasConnected & _Reconnect)
                                {
                                    // We had been connected.
                                    raisePowerOn();
                                }
                                wasConnected = true;
                            }
                        }
                        break;
                    case "CWIambic":
                        {
                            Tracing.TraceLine("CWIambic:" + r.CWIambic.ToString(), TraceLevel.Info);
                            if (r.CWIambic)
                            {
                                _Keyer = (r.CWIambicModeA) ? IambicValues.iambicA : IambicValues.iambicB;
                            }
                            else _Keyer = IambicValues.off;
                        }
                        break;
                    case "CWIambicModeA":
                        {
                            Tracing.TraceLine("CWIambicModeA:" + r.CWIambicModeA.ToString(), TraceLevel.Info);
                            if (r.CWIambic & r.CWIambicModeA) _Keyer = IambicValues.iambicA;
                        }
                        break;
                    case "CWIambicModeB":
                        {
                            Tracing.TraceLine("CWIambicModeB:" + r.CWIambicModeB.ToString(), TraceLevel.Info);
                            if (r.CWIambic & r.CWIambicModeB) _Keyer = IambicValues.iambicB;
                        }
                        break;
                    case "CWPitch":
                        {
                            Tracing.TraceLine("CWPitch:" + r.CWPitch, TraceLevel.Info);
                            if (useCWMon) CWMon.Frequency = (uint)r.CWPitch;
                        }
                        break;
                    case "CWSpeed":
                        {
                            Tracing.TraceLine("CWSpeed:" + r.CWSpeed, TraceLevel.Info);
                            if (useCWMon) CWMon.Speed = (uint)r.CWSpeed;
                        }
                        break;
                    case "CWSwapPaddles":
                        _CWReverse = r.CWSwapPaddles;
                        break;
                    case "DatabaseImportComplete":
                        {
                            Tracing.TraceLine("DatabaseImportComplete:" + r.DatabaseImportComplete.ToString(), TraceLevel.Info);
                            if (r.DatabaseImportComplete)
                            {
                                q.Enqueue((FunctionDel)(() => { GetProfileInfo(true); }));
                                RefreshMemories();
                            }
                        }
                        break;
                    case "InterlockState":
                        {
                            Tracing.TraceLine("InterlockState:" + r.InterlockState.ToString(), TraceLevel.Info);
                            base.Transmit = (r.InterlockState == InterlockState.Transmitting) ? true : false;
                        }
                        break;
                    case "Mox":
                        {
                            Tracing.TraceLine("Mox:" + r.Mox.ToString(), TraceLevel.Info);
                        }
                        break;
                    case "PanadaptersRemaining":
                        Tracing.TraceLine("PanadaptersRemaining:" + r.PanadaptersRemaining, TraceLevel.Info);
                        break;
                    case "ProfileGlobalList":
                        {
                            JJRadioDefaultLoaded = (r.ProfileGlobalList.Contains(JJRadioDefault));
                            line = "";
                            foreach (string str in r.ProfileGlobalList)
                            {
                                line += str + " ";
                            }
                            Tracing.TraceLine("ProfileGlobalList:" + line, TraceLevel.Info);
                        }
                        break;
                    case "ProfileGlobalSelection":
                        {
                            Tracing.TraceLine("ProfileGlobalSelection:" + r.ProfileGlobalSelection.ToString(), TraceLevel.Info);
                            if (r.ProfileGlobalSelection == JJRadioDefault)
                            {
                                JJRadioDefaultSelected = true;
                            }
                        }
                        break;
                    case "ProfileTXList":
                        {
                            //JJRadioDefaultLoaded = (r.ProfileGlobalList.Contains(JJRadioDefault));
                            line = "";
                            foreach (string str in r.ProfileTXList)
                            {
                                line += str + " ";
                            }
                            Tracing.TraceLine("ProfileTXList:" + line, TraceLevel.Info);
                        }
                        break;
                    case "ProfileTXSelection":
                        Tracing.TraceLine("ProfileTXSelection:" + r.ProfileTXSelection, TraceLevel.Info);
                        break;
                    case "RFPower":
                        Tracing.TraceLine("RFPower:" + theRadio.RFPower, TraceLevel.Info);
                        _XmitPower = theRadio.RFPower;
                        break;
                    case "Status":
                        string status = theRadio.Status;
                        Tracing.TraceLine("radio status:" + status, TraceLevel.Info);
                        break;
                    case "TunePower":
                        Tracing.TraceLine("TunePower:" + theRadio.TunePower, TraceLevel.Info);
                        _TunePower = theRadio.TunePower;
                        break;
                    case "TXCWMonitorGain":
                        {
                            Tracing.TraceLine("TXCWMonitorGain:" + theRadio.TXCWMonitorGain, TraceLevel.Info);
                            if (useCWMon) CWMon.Volume = theRadio.TXCWMonitorGain;
                        }
                        break;
                    case "TXTune":
                        {
                            Tracing.TraceLine("TXTune:" + r.TXTune.ToString(), TraceLevel.Info);
                        }
                        break;
                }
            }
            else if (sender is Slice)
            {
                Tracing.TraceLine("propertyChanged:Slice:" + e.PropertyName, TraceLevel.Verbose);
                Slice s = (Slice)sender;
                switch (e.PropertyName)
                {
                    case "Active":
                        {
                            Tracing.TraceLine("Active:slice " + s.Index.ToString() + " " + s.Active.ToString(), TraceLevel.Info);
                            if (s.Active)
                            {
                                _RXFrequency = LibFreqtoLong(s.Freq);
                                if (RXFreqChange != null) RXFreqChange(s);
#if CWMonitor
                                if (useCWMon && (s == VFOToSlice(TXVFO)) && (s.DemodMode == "CW"))
                                {
                                    CWMonStart(); // ok if already started.
                                }
#endif
                            }
                        }
                        break;
                    case "DemodMode":
                        {
                            Tracing.TraceLine("DemodMode:slice " + s.Index.ToString() + " " + s.DemodMode.ToString(), TraceLevel.Info);
                            //if (s.Active) _RXMode = getMode(s.DemodMode);
                            //if (s.Transmit) _TXMode = getMode(s.DemodMode);
                            if (s.Active && (RXFreqChange != null)) RXFreqChange(s);
#if CWMonitor
                            try
                            {
                                if (useCWMon && (s == VFOToSlice(TXVFO)))
                                {
                                    if (s.DemodMode == "CW") CWMonStart();
                                    else CWMonStop();
                                }
                            }
                            catch { }
#endif
                        }
                        break;
                    case "Freq":
                        {
                            Tracing.TraceLine("Freq:slice " + s.Index.ToString() + " " + s.Freq.ToString(), TraceLevel.Verbose);
                            if (s.Active)
                            {
                                _RXFrequency = LibFreqtoLong(s.Freq);
                                if (RXFreqChange != null) RXFreqChange(s);
                            }
                            if (s.IsTransmitSlice) _TXFrequency = LibFreqtoLong(s.Freq);
                        }
                        break;
                    case "IsTransmitSlice":
                        {
                            Tracing.TraceLine("Transmit:slice " + s.Index.ToString() + " " + s.IsTransmitSlice.ToString(), TraceLevel.Info);
                            RigCaps.VFOs vfo = SliceToVFO(s);
                            if (s.IsTransmitSlice)
                            {
                                _TXVFO = vfo;
                                _TXFrequency = LibFreqtoLong(s.Freq);
                            }
                        }
                        break;
                    case "Mute":
                        {
                            if (LANAudio)
                            {
                                int id = findAudioChannelBySlice(s).ID;
                                if (s.Mute) stopLocalAudioChannel(id);
                                else startLocalAudioChannel(id);
                            }
                        }
                        break;
                    case "NBLevel":
                        {
                            Tracing.TraceLine("slice NBLevel:" + s.NBLevel.ToString(), TraceLevel.Info);
                            //s.Panadapter.NBLevel = s.NBLevel;
                        }
                        break;
                    case "NBOn":
                        {
                            Tracing.TraceLine("slice NBOn:" + s.NBOn.ToString(), TraceLevel.Info);
                            //s.Panadapter.NBOn = s.NBOn;
                        }
                        break;
                    case "RITOn":
                        {
                            Tracing.TraceLine("RITOn:" + s.RITOn.ToString(), TraceLevel.Info);
                            lock (_RIT)
                            {
                                _RIT.Active = s.RITOn;
                            }
                        }
                        break;
                    case "RITFreq":
                        {
                            Tracing.TraceLine("RITFreq:" + s.RITFreq.ToString(), TraceLevel.Info);
                            lock (_RIT)
                            {
                                _RIT.Value = s.RITFreq;
                            }
                        }
                        break;
                    case "XITOn":
                        {
                            Tracing.TraceLine("XITOn:" + s.XITOn.ToString(), TraceLevel.Info);
                            lock (_XIT)
                            {
                                _XIT.Active = s.XITOn;
                            }
                        }
                        break;
                    case "XITFreq":
                        {
                            Tracing.TraceLine("XITFreq:" + s.XITFreq.ToString(), TraceLevel.Info);
                            lock (_XIT)
                            {
                                _XIT.Value = s.XITFreq;
                            }
                        }
                        break;
#if zero
                    case "TXAntenna":
                        Tracing.TraceLine("TXAntenna:" + s.TXAnt, TraceLevel.Info);
                        // We always set the TXAnt for both slices, so we'll come through twice.
                        break;
#endif
                }
            }
            else if (sender is Panadapter)
            {
                Tracing.TraceLine("propertyChanged:Panadapter:" + e.PropertyName, TraceLevel.Verbose);
                Panadapter p = (Panadapter)sender;
                switch (e.PropertyName)
                {
                    case "RFGain":
                        Tracing.TraceLine("panadapter RFGain:" + p.RFGain.ToString(), TraceLevel.Verbose);
                        _PreAmp = (p.RFGain == PreAmpMax) ? OffOnValues.on : OffOnValues.off;
                        break;
                    case "Bandwidth":
                        Tracing.TraceLine("Bandwidth:" + p.Bandwidth.ToString(), TraceLevel.Verbose);
                        break;
                    case "CenterFreq":
                        Tracing.TraceLine("CenterFreq:" + p.CenterFreq.ToString(), TraceLevel.Verbose);
                        break;
                    case "FPS":
                        Tracing.TraceLine("FPS:" + p.FPS.ToString(), TraceLevel.Verbose);
                        break;
                    case "HighDbm":
                        Tracing.TraceLine("HighDBM:" + p.HighDbm.ToString(), TraceLevel.Verbose);
                        break;
                    case "LowDbm":
                        Tracing.TraceLine("LowDbm:" + p.LowDbm.ToString(), TraceLevel.Verbose);
                        break;
                }
            }
            else if (sender is TNF)
            {
                Tracing.TraceLine("propertyChanged:TNF:" + e.PropertyName, TraceLevel.Verbose);
                // See FlexTNF.cs.
                TNF tnf = (TNF)sender;
                if (UpdateConfiguredTNFs != null) UpdateConfiguredTNFs(tnf);
            }
#if zero
            else if (sender is Waterfall)
            {
                Tracing.TraceLine("propertyChanged:Waterfall:" + e.PropertyName, TraceLevel.Verbose);
                Waterfall w = (Waterfall)sender;
                switch (e.PropertyName)
                {
                    case "FallLineDurationMs":
                        Tracing.TraceLine("FallLineDurationMs:" + w.FallLineDurationMs.ToString(), TraceLevel.Info);
                        break;
                }
            }
#endif
        }

        private void messageReceivedHandler(MessageSeverity severity, string message)
        {
            Tracing.TraceLine("message severity:" + severity.ToString() + " " + message, TraceLevel.Error);
        }

        private Slice.SMeterDataReadyEventHandler[] sMeterHandlers = new Slice.SMeterDataReadyEventHandler[2];
        private int sliceCount { get { return theRadio.SliceList.Count; } }
        private void sliceAdded(Slice slc)
        {
            Tracing.TraceLine("sliceAdded:" + sliceCount.ToString() + ':' + slc.ToString(), TraceLevel.Info);
            slc.PropertyChanged += propertyChangedHandler;
            slc.MeterAdded += meterAdded;
            if (slc.Index == 0) slc.SMeterDataReady += sMeterHandler0;
            else if (slc.Index == 1) slc.SMeterDataReady += sMeterHandler1;
        }

        private void sliceRemoved(Slice slc)
        {
            Tracing.TraceLine("sliceRemoved:" + sliceCount.ToString() + ':' + slc.ToString(), TraceLevel.Info);
        }

        internal delegate void PanSetupDel();
        /// <summary>
        /// Provided by the user control to setup the pan adapter.
        /// </summary>
        internal PanSetupDel PanSetup;
        internal Panadapter Panadapter
        {
            get
            {
                Panadapter rv = null;
                if (theRadio.ActiveSlice != null) rv = theRadio.ActiveSlice.Panadapter;
                return rv;
            }
        }
        private List<Waterfall> waterfallList;
        internal Waterfall Waterfall
        {
            get
            {
                return GetPanadaptersWaterfall(Panadapter);
            }
        }
        internal Waterfall GetPanadaptersWaterfall(Panadapter pan)
        {
            Waterfall rv = null;
            if ((pan != null) && (waterfallList != null))
            {
                foreach (Waterfall w in waterfallList)
                {
                    if (w.StreamID == pan.ChildWaterfallStreamID)
                    {
                        rv = w;
                        break;
                    }
                }
            }
            return rv;
        }
        private int panCount { get { return theRadio.PanadapterList.Count; } }
        private void panadapterAdded(Panadapter pan, Waterfall fall)
        {
            if (waterfallList == null) waterfallList = new List<Waterfall>();
            waterfallList.Add(fall);
            pan.Width = 5000;
            pan.PropertyChanged += propertyChangedHandler;
            fall.PropertyChanged += propertyChangedHandler;
            Tracing.TraceLine("panadapterAdded:" + panCount.ToString() + ':' + pan.ToString(), TraceLevel.Info);
        }

        private void panAdapterRemoved(Panadapter pan)
        {
            Tracing.TraceLine("panadapterRemoved:" + panCount.ToString() + ':' + pan.ToString(), TraceLevel.Info);
        }
        private void waterfallRemoved(Waterfall fall)
        {
            Tracing.TraceLine("waterfallRemoved", TraceLevel.Info);
            if ((waterfallList != null) && waterfallList.Contains(fall)) waterfallList.Remove(fall);
        }
#if zero
        // Doesn't remove the pan from the radio.
        private void panadapterRemove(Panadapter p)
        {
            p.PropertyChanged -= propertyChangedHandler;
            p.Close(false);
        }
#endif

        internal List<TNF> TNFs
        {
            get { return theRadio.TNFList; }
        }
        private void tnfAdded(TNF tnf)
        {
            Tracing.TraceLine("tnfAdded:" + tnf.ToString(), TraceLevel.Info);
            tnf.PropertyChanged += propertyChangedHandler;
            // No need to call the update, since created TNFs are not permanent.
        }
        private void tnfRemoved(TNF tnf)
        {
            Tracing.TraceLine("tnfRemove:" + tnf.ID.ToString(), TraceLevel.Info);
            // Don't call UpdateConfiguredTNFs here.
            //if ((UpdateConfiguredTNFs != null) & !Closing) UpdateConfiguredTNFs(tnf, true);
        }

        protected int DBmToPower(float dbm)
        {
            return (int)((Math.Pow(10d, (double)(dbm / 10)) / 1000) + 0.5);
        }
        protected float _PowerDBM;
        private void forwardPowerData(float data)
        {
            Tracing.TraceLine("forwardPower:" + data.ToString(), TraceLevel.Verbose);
            if (_PowerDBM != data)
            {
                _PowerDBM = data;
            }
        }

        protected float _SWR;
        internal float SWR { get { return _SWR; } }

        private void sWRData(float data)
        {
            Tracing.TraceLine("SWRData:" + data.ToString(), TraceLevel.Verbose);
            _SWR = data;
        }

        private string SWRText()
        {
            return _SWR.ToString();
        }

        private void micData(float data)
        {
            Tracing.TraceLine("micData:" + data.ToString(), TraceLevel.Verbose);
        }

        internal float _MicPeakData;
        private void micPeakData(float data)
        {
            Tracing.TraceLine("micPeakData:" + data.ToString(), TraceLevel.Verbose);
            _MicPeakData = data;
        }

        private void compPeakData(float data)
        {
            Tracing.TraceLine("compPeakData:" + data.ToString(), TraceLevel.Verbose);
        }

        private void hwALCData(float data)
        {
            Tracing.TraceLine("hwALCData:" + data.ToString(), TraceLevel.Verbose);
        }

        private void meterAdded(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterAdded:slice " + slc.Index.ToString() + ' ' + m.ToString(), TraceLevel.Info);
        }

        private void meterRemoved(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterRemoved:" + m.ToString(), TraceLevel.Info);
        }

        private void sMeterHandler0(float data)
        {
            if ((theRadio.SliceList != null) && (theRadio.SliceList.Count > 0) && theRadio.SliceList[0].Active) sMeterData(data);
        }
        private void sMeterHandler1(float data)
        {
            if ((theRadio.SliceList != null) && (theRadio.SliceList.Count > 1) && theRadio.SliceList[1].Active) sMeterData(data);
        }
        private void sMeterData(float data)
        {
            Tracing.TraceLine("sMeterData:" + data.ToString(), TraceLevel.Verbose);
            _SMeter = (int)data;
        }

        public override bool Transmit
        {
            get
            {
                return base.Transmit;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.Mox = value; }));
            }
        }

        private int firstCharID = -1;
        private StringBuilder sentChars = new StringBuilder();
        private void charSentHandler(int id)
        {
            Tracing.TraceLine("charSent:" + id, TraceLevel.Info);
            if (firstCharID == -1) firstCharID = id;
            int currentCharID = id - firstCharID;
#if CWMonitor
            if (useCWMon && CWMon.Started)
            {
                CWMon.Send(sentChars[currentCharID]);
                //if (currentCharID < (sentChars.Length - 1)) CWMon.Send(sentChars[currentCharID + 1]);
            }
#endif
        }

        internal int VFOToSliceID(RigCaps.VFOs vfo)
        {
            return (int)vfo;
        }
        internal Slice VFOToSlice(RigCaps.VFOs vfo)
        {
#if zero
            Slice rv = null;
            await(() => { return ((int)vfo < theRadio.SliceList.Count); }, 1000);
            try
            {
                rv = theRadio.SliceList[VFOToSliceID(vfo)];
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("VFOToSlice exception:" + vfo.ToString() + ' ' + theRadio.SliceList.Count.ToString());
                Tracing.ErrMessageTrace(ex, true);
            }
            return rv;
#else
            //return theRadio.SliceList[VFOToSliceID(vfo)];
            Slice rv = theRadio.SliceList[0];
            try { rv = theRadio.SliceList[VFOToSliceID(vfo)]; }
            catch (Exception ex)
            {
                Tracing.TraceLine("VFOToSlice exception on vfo " + vfo.ToString() + ':' + ex.Message, TraceLevel.Error);
            }
            return rv;
#endif
        }
        internal RigCaps.VFOs SliceIDToVFO(int id)
        {
            return (RigCaps.VFOs)id;
        }
        internal RigCaps.VFOs SliceToVFO(Slice s)
        {
            RigCaps.VFOs rv = RigCaps.VFOs.VFOA;
            try
            {
                rv = SliceIDToVFO(s.Index);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("SliceToVFO exception: " + ex.Message, TraceLevel.Error);
            }
            return rv;
        }

        // Note that VFOToSlice(RXVFO) is the same as theRadio.ActiveSlice.
        public override RigCaps.VFOs RXVFO
        {
            get
            {
                //return SliceToVFO(theRadio.ActiveSlice);
                return base.RXVFO;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { VFOToSlice(value).Active = true; }));
            }
        }

        public override RigCaps.VFOs TXVFO
        {
            get
            {
                return base.TXVFO;
            }
            set
            {
                //_TXVFO = value;
                q.Enqueue((FunctionDel)(() => { VFOToSlice(value).IsTransmitSlice = true; }));
            }
        }

        // We just copy the frequency and mode now.
        public override void CopyVFO(RigCaps.VFOs inv, RigCaps.VFOs outv)
        {
            if ((inv != CurVFO) & (outv != nextVFO(inv)))
            {
                Tracing.TraceLine("CopyVFO:VFOs are set wrong.", TraceLevel.Error);
                return;
            }
            if (Transmit)
            {
                Tracing.TraceLine("CopyVFO:can't be transmitting", TraceLevel.Error);
                return;
            }
            Tracing.TraceLine("CopyVFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Info);
            Slice inSlice = VFOToSlice(inv);
            Slice outSlice = VFOToSlice(outv);
            q.Enqueue((FunctionDel)(() => { outSlice.Freq = inSlice.Freq; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DemodMode = inSlice.DemodMode; }));
            List<Slice> sList = new List<Slice>();
            sList.Add(inSlice);
            sList.Add(outSlice);
            if (RXFreqChange != null) RXFreqChange(sList);
        }

        public override bool SplitShowXmitFrequency
        {
            get
            {
                return base.SplitShowXmitFrequency;
            }
            set
            {
                if (!Transmit)
                {
                    if (Split) TFSetOn = value;
                }
            }
        }

        internal double LongFreqToLibFreq(ulong u)
        {
            return (double)u / 1000000d;
        }

        internal ulong LibFreqtoLong(double f)
        {
            return (ulong)(f * 1000000d);
        }

        public override ulong RXFrequency
        {
            get
            {
                //double freq = (TFSetOn) ? VFOToSlice(TXVFO).Freq : VFOToSlice(RXVFO).Freq;
                //return LibFreqtoLong(freq);
                return (TFSetOn) ? _TXFrequency : _RXFrequency;
            }
            set
            {
                double freq = LongFreqToLibFreq(value);
                if (TFSetOn) q.Enqueue((FunctionDel)(() => { VFOToSlice(TXVFO).Freq = freq; }));
                else q.Enqueue((FunctionDel)(() => { VFOToSlice(RXVFO).Freq = freq; }));
                //_RXFrequency = value;
            }
        }
        public override ulong TXFrequency
        {
            get
            {
                //return LibFreqtoLong(VFOToSlice(TXVFO).Freq);
                return base.TXFrequency;
            }
            set
            {
                double freq = LongFreqToLibFreq(value);
                q.Enqueue((FunctionDel)(() => { VFOToSlice(TXVFO).Freq = freq; }));
            }
        }

        public override bool Split
        {
            get
            {
                return (RXVFO != TXVFO);
            }
            set
            {
                if (!IsMemoryMode(RXVFO) && !TFSetOn)
                {
                    // Using VFOs.
                    RigCaps.VFOs v = (value) ? nextVFO(RXVFO) : RXVFO;
                    Slice s = VFOToSlice(v);
                    q.Enqueue((FunctionDel)(() => { s.IsTransmitSlice = true; }));
                    //_TXVFO = v;
                }
                // Else using a memory or TFSetOn, can't set it.
            }
        }

        public override ModeValue RXMode
        {
            get
            {
                return getMode(theRadio.ActiveSlice.DemodMode);
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DemodMode = FlexMode(value); }));
            }
        }
        public override ModeValue TXMode
        {
            get
            {
                return getMode(VFOToSlice(TXVFO).DemodMode);
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { VFOToSlice(TXVFO).DemodMode = FlexMode(value); }));
            }
        }

        internal int FilterLow
        {
            get
            {
                return theRadio.ActiveSlice.FilterLow;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterLow = value; }));
            }
        }
        internal int FilterHigh
        {
            get
            {
                return theRadio.ActiveSlice.FilterHigh;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterHigh = value; }));
            }
        }

        // TXAntenna must be set first.
        public override bool RXAntenna
        {
            get
            {
                return (theRadio.ActiveSlice.RXAnt != VFOToSlice(RXVFO).TXAnt);
            }
            set
            {
                // Use the other antenna, 0 or 1, if true.
                int ant = (value) ? (TXAntenna + 1) % 2 : TXAntenna;
                q.Enqueue((FunctionDel)(() => { theRadio.SliceList[0].RXAnt = theRadio.RXAntList[ant]; }));
                q.Enqueue((FunctionDel)(() => { theRadio.SliceList[1].RXAnt = theRadio.RXAntList[ant]; }));
            }
        }

        /// <summary>
        /// Set both the TX and RX antenna values.
        /// </summary>
        public override int TXAntenna
        {
            get
            {
                int rv = -1; // Invalid if not found.
                int max = Math.Min(theRadio.RXAntList.Length - 1, 1);
                for (int id = 0; id <= max; id++)
                {
                    if (theRadio.ActiveSlice.TXAnt == theRadio.RXAntList[id])
                    {
                        rv = id;
                        break;
                    }
                }
                return rv;
            }
            set
            {
                Tracing.TraceLine("TXAntenna:" + value.ToString(), TraceLevel.Info);
                if (value < theRadio.RXAntList.Length)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.SliceList[0].TXAnt = theRadio.RXAntList[value]; }));
                    q.Enqueue((FunctionDel)(() => { theRadio.SliceList[1].TXAnt = theRadio.RXAntList[value]; }));
                    q.Enqueue((FunctionDel)(() => { theRadio.SliceList[0].RXAnt = theRadio.RXAntList[value]; }));
                    q.Enqueue((FunctionDel)(() => { theRadio.SliceList[1].RXAnt = theRadio.RXAntList[value]; }));
                }
            }
        }

        protected void setFlexTunerTypeNotAuto()
        {
            _FlexTunerType = (myCaps.HasCap(RigCaps.Caps.ManualATGet)) ?
                FlexTunerTypes.manual : FlexTunerTypes.none;
            Tracing.TraceLine("setFlexTunerTypeNotAuto:" + _FlexTunerType.ToString(), TraceLevel.Info);
        }

        public override FlexTunerTypes FlexTunerType
        {
            get { return _FlexTunerType; }
            set
            {
                // Set by the user only.
                Tracing.TraceLine("FlexTunerType:" + value.ToString() + ' ' +
                    _FlexTunerType.ToString() + ' ' + _FlexTunerOn.ToString(), TraceLevel.Info);
                if (value == _FlexTunerType) return;
                // Can't change while tuning.
                if (!_FlexTunerOn)
                {
                    if (value == FlexTunerTypes.auto) _FlexTunerType = value;
                    else
                    {
                        setFlexTunerTypeNotAuto();
                        // We were in autoTune mode.  Need to bypass.
                        theRadio.ATUTuneBypass();
                    }
                }
            }
        }

        public override bool FlexTunerOn
        {
            get { return _FlexTunerOn; }
            set
            {
                // set internally or by the user.
                Tracing.TraceLine("FlexTunerOn:" + value.ToString() + ' ' +
                    _FlexTunerOn.ToString() + ' ' + _FlexTunerType.ToString(), TraceLevel.Info);
                if (value == _FlexTunerOn) return;
                switch (_FlexTunerType)
                {
                    case FlexTunerTypes.manual:
                        {
                            // Report status if turning off.
                            if (!value)
                            {
                                RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                    (FlexTunerType, ATUTuneStatus.OK, SWR));
                            }
                            q.Enqueue((FunctionDel)(() => { theRadio.TXTune = value; }));
                        }
                        break;
                    case FlexTunerTypes.auto:
                        {
                            // Normally tuning stops automatically when finished.
                            q.Enqueue((FunctionDel)(() => { Transmit = value; }));
                            if (value)
                            {
                                q.Enqueue((FunctionDel)(() => { theRadio.ATUTuneStart(); }));
                            }
                        }
                        break;
                }
                _FlexTunerOn = value;
            }
        }

        public override void AntennaTunerMemories()
        {
            Form theForm = new FlexATUMemories(this);
            theForm.ShowDialog();
            theForm.Dispose();
        }

        public override bool FlexTunerUsingMemoryNow
        {
            get
            {
                return ((_FlexTunerType == FlexTunerTypes.auto) &
                    (theRadio.ATUTuneStatus != ATUTuneStatus.Bypass) &
                    theRadio.ATUMemoriesEnabled & theRadio.ATUUsingMemory);
            }
        }

#if zero
        public override OffOnValues ManualTuner
        {
            get
            {
                return (theRadio.TXTune) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXTune = (value == OffOnValues.on); }));
            }
        }
#endif

        internal const int AudioGainMinValue = 0;
        internal const int AudioGainMaxValue = 100;
        public override int  AudioGain
        {
            get
            {
                //return base.AudioGain;
                return theRadio.ActiveSlice.AudioGain;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioGain = value; }));
            }
        }

        internal const int AudioPanMinValue = 0;
        internal const int AudioPanMaxValue = 100;
        public int AudioPan
        {
            get
            {
                return theRadio.ActiveSlice.AudioPan;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioPan = value; }));
            }
        }

        internal const int LineoutGainMinValue = 0;
        internal const int LineoutGainMaxValue = 100;
        public override int LineoutGain
        {
            get
            {
                //return base.LineoutGain;
                return theRadio.LineoutGain;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.LineoutGain = value; }));
            }
        }

        internal const int HeadphoneGainMinValue = 0;
        internal const int HeadphoneGainMaxValue = 100;
        public override int HeadphoneGain
        {
            get
            {
                //return base.HeadphoneGain;
                return theRadio.HeadphoneGain;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.HeadphoneGain = value; }));
            }
        }

        public override OffOnValues Vox
        {
            get
            {
                //return base.Vox;
                bool val;
                if (VFOToSlice(TXVFO).DemodMode == "CW") val = theRadio.CWBreakIn;
                else val = theRadio.SimpleVOXEnable;
                return (val) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                Slice s = VFOToSlice(TXVFO);
                bool val = (value == OffOnValues.on) ? true : false;
                if (s.DemodMode == "CW")
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.CWBreakIn = val; }));
                }
                else
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXEnable = val; }));
                }
            }
        }

        public override OffOnValues NoiseBlanker
        {
            get
            {
                return (theRadio.ActiveSlice.NBOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBOn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        // The values are the same for the wide band NB.
        internal const int NoiseBlankerValueMin = 0;
        internal const int NoiseBlankerValueMax = 100;
        internal const int NoiseBlankerValueIncrement = 5;
        internal int NoiseBlankerLevel
        {
            get { return theRadio.ActiveSlice.NBLevel; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBLevel = value; })); }
        }

        internal OffOnValues WidebandNoiseBlanker
        {
            get
            {
                return (theRadio.ActiveSlice.WNBOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBOn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        internal int WidebandNoiseBlankerLevel
        {
            get { return theRadio.ActiveSlice.WNBLevel; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBLevel = value; })); }
        }

        public override OffOnValues NoiseReduction
        {
            get
            {
                return (theRadio.ActiveSlice.NROn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NROn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        internal const int NoiseReductionValueMin = 0;
        internal const int NoiseReductionValueMax = 100;
        internal const int NoiseReductionValueIncrement = 5;
        internal int NoiseReductionLevel
        {
            get { return theRadio.ActiveSlice.NRLevel; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLevel = value; })); }
        }

        /// <summary>
        /// AGC mode
        /// </summary>
        /// <remarks>Different from AllRadios</remarks>
        internal AGCMode AGCSpeed
        {
            get { return theRadio.ActiveSlice.AGCMode; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCMode = value; })); }
        }

        internal const int AGCThresholdMin = 0;
        internal const int AGCThresholdMax = 100;
        internal const int AGCThresholdIncrement = 5;
        internal int AGCThreshold
        {
            get { return theRadio.ActiveSlice.AGCThreshold; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCThreshold = value; })); }
        }

        public override RITData RIT
        {
            get
            {
                RITData r = new RITData();
                lock (_RIT)
                {
                    r.Active = _RIT.Active;
                    r.Value = _RIT.Value;
                }
                return r;
            }
            set
            {
                // _RIT set in PropertyChangedHandler
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITOn = value.Active; }));
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITFreq = value.Value; }));
            }
        }

        public override RITData XIT
        {
            get
            {
                RITData x = new RITData();
                lock (_XIT)
                {
                    x.Active = _XIT.Active;
                    x.Value = _XIT.Value;
                }
                return x;
            }
            set
            {
                // _XIT set in PropertyChangedHandler
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITFreq = value.Value; }));
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITOn = value.Active; }));
            }
        }

        internal const int BreakinDelayMin = 0;
        internal const int BreakinDelayMax = 2000;
        internal const int BreakinDelayIncrement = 50;
        internal int BreakinDelay
        {
            get { return theRadio.CWDelay; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWDelay = value; }));
                q.Enqueue((FunctionDel)(() => { cwx.Delay = value; }));
            }
        }

        internal const int SidetonePitchMin = 100;
        internal const int SidetonePitchMax = 6000;
        internal const int SidetonePitchIncrement = 50;
        internal int SidetonePitch
        {
            get { return theRadio.CWPitch; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.CWPitch = value; })); }
        }

        internal const int SidetoneGainMin = 0;
        internal const int SidetoneGainMax = 100;
        internal const int SidetoneGainIncrement = 5;
        internal int SidetoneGain
        {
            get { return theRadio.TXCWMonitorGain; }
            set
            {
                if (useCWMon) q.Enqueue((FunctionDel)(() => { CWMon.Volume = value; }));
                q.Enqueue((FunctionDel)(() => { theRadio.TXCWMonitorGain = value; }));
            }
        }

        internal enum IambicValues
        {
            off,
            iambicA,
            iambicB
        }

        private IambicValues _Keyer;
        internal IambicValues Keyer
        {
            get { return _Keyer; }
            set
            {
                // Set keyer on/off
                q.Enqueue((FunctionDel)(() => { theRadio.CWIambic = (value == IambicValues.off) ? false : true; }));
                // Set iambic mode.
                q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeA = (value == IambicValues.iambicA) ? true : false; }));
                q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeB = (value == IambicValues.iambicB) ? true : false; }));
            }
        }

        private bool _CWReverse;
        internal bool CWReverse
        {
            get { return _CWReverse; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWSwapPaddles = value; }));
            }
        }

        internal const int KeyerSpeedMin = 5;
        internal const int KeyerSpeedMax = 100;
        internal const int KeyerSpeedIncrement = 1;
        internal int KeyerSpeed
        {
            get { return theRadio.CWSpeed; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWSpeed = value; }));
                q.Enqueue((FunctionDel)(() => { cwx.Speed = value; }));
            }
        }

        internal AllRadios.OffOnValues CWL
        {
            get { return (theRadio.CWL_Enabled) ? AllRadios.OffOnValues.on : AllRadios.OffOnValues.off; }
            set
            {
                bool val = (value == AllRadios.OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.CWL_Enabled = val; }));
            }
        }

        internal const int MonitorPanMin = 0;
        internal const int MonitorPanMax = 100;
        internal const int MonitorPanIncrement = 5;
        internal int MonitorPan
        {
            get { return theRadio.TXCWMonitorPan; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.TXCWMonitorPan = value; })); }
        }

        internal const int MicGainMin = 0;
        internal const int MicGainMax = 100;
        internal const int MicGainIncrement = 1;
        internal int MicGain
        {
            get { return theRadio.MicLevel; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.MicLevel = value; })); }
        }

        internal OffOnValues ProcessorOn
        {
            get { return (theRadio.SpeechProcessorEnable) ? OffOnValues.on : OffOnValues.off; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorEnable = (value == OffOnValues.on) ? true : false; })); }
        }

        internal enum ProcessorSettings
        {
            NOR = 0,
            DX,
            DXX,
        }
        internal ProcessorSettings ProcessorSetting
        {
            get { return (ProcessorSettings)theRadio.SpeechProcessorLevel; }
            set { q.Enqueue((FunctionDel)(() => { theRadio.SpeechProcessorLevel = (uint)value; })); }
        }

        internal AllRadios.OffOnValues Compander
        {
            get { return (theRadio.CompanderOn) ? AllRadios.OffOnValues.on : AllRadios.OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.CompanderOn = val; }));
            }
        }

        internal const int CompanderLevelMin = 0;
        internal const int CompanderLevelMax = 100;
        internal const int CompanderLevelIncrement = 5;
        internal int CompanderLevel
        {
            get { return theRadio.CompanderLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CompanderLevel = value; }));
            }
        }

        internal int TXFilterLowMin = 0;
        internal int TXFilterLowMax { get { return (TXFilterHigh - 50); } }
        internal int TXFilterLowIncrement = 50;
        internal int TXFilterLow
        {
            get { return (theRadio != null) ? theRadio.TXFilterLow : 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXFilterLow = value; }));
            }
        }

        internal int TXFilterHighMin { get { return (TXFilterLow + 50); } }
        internal int TXFilterHighMax = 10000;
        internal int TXFilterHighIncrement = 50;
        internal int TXFilterHigh
        {
            get { return (theRadio != null) ? theRadio.TXFilterHigh : 0; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXFilterHigh = value; }));
            }
        }

        internal OffOnValues MicBoost
        {
            get { return (theRadio.MicBoost) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.MicBoost = val; }));
            }
        }

        internal OffOnValues MicBias
        {
            get { return (theRadio.MicBias) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.MicBias = val; }));
            }
        }

        internal AllRadios.OffOnValues Monitor
        {
            get { return (theRadio.TXMonitor) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.TXMonitor = val; }));
            }
        }

        internal const int SBMonitorLevelMin = 0;
        internal const int SBMonitorLevelMax = 100;
        internal const int SBMonitorLevelIncrement = 5;
        internal int SBMonitorLevel
        {
            get { return theRadio.TXSBMonitorGain; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXSBMonitorGain = value; }));
            }
        }

        internal const int SBMonitorPanMin = 0;
        internal const int SBMonitorPanMax = 100;
        internal const int SBMonitorPanIncrement = 5;
        internal int SBMonitorPan
        {
            get { return theRadio.TXSBMonitorPan; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXSBMonitorPan = value; }));
            }
        }

        // transmit power
        internal const int XmitPowerMin = 0;
        internal const int XmitPowerMax = 100;
        internal const int XmitPowerIncrement = 5;
        public override int XmitPower
        {
            get
            {
                return base.XmitPower;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.RFPower = value; }));
            }
        }

        // Tuning power
        internal const int TunePowerMin = 0;
        internal const int TunePowerMax = 100;
        internal const int TunePowerIncrement = 1;
        public override int TunePower
        {
            get
            {
                return base.TunePower;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TunePower = value; }));
            }
        }

        // Vox delay is in MS, with 50 MS per step, see FlexLib.Radio.cs
        internal const int VoxDelayMin = 0;
        internal const int VoxDelayMax = 2000;
        internal const int VoxDelayIncrement = 100;
        internal const int VoxDelayMS = 50;
        internal int VoxDelay
        {
            get { return theRadio.SimpleVOXDelay * VoxDelayMS; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXDelay = value / VoxDelayMS; }));
            }
        }

        internal const int VoxGainMin = 0;
        internal const int VoxGainMax = 100;
        internal const int VoxGainIncrement = 5;
        internal int VoxGain
        {
            get { return theRadio.SimpleVOXLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXLevel = value; }));
            }
        }

        internal AllRadios.OffOnValues ANF
        {
            get { return (theRadio.ActiveSlice.ANFOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFOn = val; }));
            }
        }

        internal const int AutoNotchLevelMin = 0;
        internal const int AutoNotchLevelMax = 100;
        internal const int AutoNotchLevelIncrement = 10;
        internal int AutoNotchLevel
        {
            get { return theRadio.ActiveSlice.ANFLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLevel = value; }));
            }
        }

        internal AllRadios.OffOnValues APF
        {
            get { return (theRadio.ActiveSlice.APFOn) ? AllRadios.OffOnValues.on : AllRadios.OffOnValues.off; }
            //get { return (theRadio.APFMode) ? AllRadios.OffOnValues.on : AllRadios.OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFOn = val; }));
                //q.Enqueue((FunctionDel)(() => { theRadio.APFMode = val; }));
            }
        }

        internal const int AutoPeakLevelMin = 0;
        internal const int AutoPeakLevelMax = 100;
        internal const int AutoPeakLevelIncrement = 10;
        internal int AutoPeakLevel
        {
            get { return theRadio.ActiveSlice.APFLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFLevel = value; }));
            }
        }

        private Panadapter activePan
        {
            get { return theRadio.ActiveSlice.Panadapter; }
        }

        private const int PreAmpMin = 0;
        private const int PreAmpMax = 20;
        private OffOnValues _PreAmp;
        internal OffOnValues PreAmp
        {
            get { return _PreAmp; }
            set
            {
                // _PreAmp changed by interrupt.
                activePan.RFGain = (value == OffOnValues.on) ? PreAmpMax : PreAmpMin;
            }
        }

#if zero
        internal const int RFGainMin = -10;
        internal const int RFGainMax = 30;
        internal const int RFGainIncrement = 10;
        internal int RFGain
        {
            get { return theRadio.ActiveSlice.RFGain; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RFGain = value; }));
            }
        }

        internal const int PanRFMin = 0;
        internal const int PanRFMax = 20;
        internal const int PanRFIncrement = 20;
        internal int PanRF
        {
            get { return theRadio.ActiveSlice.Panadapter.RFGain; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.Panadapter.RFGain = value; }));
            }
        }
#endif

        internal const int AutoPeakQMin = 0;
        internal const int AutoPeakQMax = 33;
        internal const int AutoPeakQIncrement = 1;
        internal int AutoPeakQ
        {
            get { return (int)theRadio.APFQFactor; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.APFQFactor = (double)value; }));
            }
        }

        internal bool TNF
        {
            get { return theRadio.TNFEnabled; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TNFEnabled = value; }));
            }
        }

        internal OffOnValues Squelch
        {
            get { return (theRadio.ActiveSlice.SquelchOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.SquelchOn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        internal const int SquelchLevelMin = 0;
        internal const int SquelchLevelMax = 100;
        internal const int SquelchLevelIncrement = 5;
        internal int SquelchLevel
        {
            get { return theRadio.ActiveSlice.SquelchLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.SquelchLevel = value; }));
            }
        }

        internal OffsetDirections FlexOffsetDirectionToOffsetDirection(FMTXOffsetDirection dir)
        {
            OffsetDirections rv = OffsetDirections.off;
            switch (dir)
            {
                case FMTXOffsetDirection.Down: rv = OffsetDirections.minus; break;
                case FMTXOffsetDirection.Up: rv = OffsetDirections.plus; break;
            }
            return rv;
        }
        internal FMTXOffsetDirection OffsetDirectionToFlexOffsetDirection(OffsetDirections dir)
        {
            FMTXOffsetDirection rv = FMTXOffsetDirection.Simplex;
            switch (dir)
            {
                case OffsetDirections.minus: rv = FMTXOffsetDirection.Down; break;
                case OffsetDirections.plus: rv = FMTXOffsetDirection.Up; break;
            }
            return rv;
        }
        public override OffsetDirections OffsetDirection
        {
            get
            {
                return FlexOffsetDirectionToOffsetDirection(theRadio.ActiveSlice.RepeaterOffsetDirection);
            }
            set
            {
                FMTXOffsetDirection val = OffsetDirectionToFlexOffsetDirection(value);
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RepeaterOffsetDirection = val; }));
            }
        }

        internal OffOnValues FMEmphasis
        {
            get { return (theRadio.ActiveSlice.DFMPreDeEmphasis) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DFMPreDeEmphasis = val; }));
            }
        }

        // Note the Flex frequency is in MhZ, and ours in KHZ.
        internal const int offsetMin = 50;
        internal const int offsetMax = 2000;
        internal const int offsetIncrement = 50;
        public override int OffsetFrequency
        {
            get { return (int)(theRadio.ActiveSlice.FMRepeaterOffsetFreq * 1e3); }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMRepeaterOffsetFreq = (double)value / 1e3; }));
            }
        }

        // Valid FM tone modes for this rig, see FMToneMode in Memory.cs in FlexLib.
        internal static ToneCTCSSValue[] myFMToneModes =
        {
            new ToneCTCSSValue('0', "Off"),
            new ToneCTCSSValue('1', "CTCSS"),
        };
        internal ToneCTCSSValue ToneModeToToneCTCSS(FMToneMode mode)
        {
            return myFMToneModes[(int)mode];
        }
        internal FMToneMode ToneCTCSSToToneMode(ToneCTCSSValue val)
        {
            return (FMToneMode)(val.value - 0x30);
        }
        public override ToneCTCSSValue ToneCTCSS
        {
            get { return ToneModeToToneCTCSS(theRadio.ActiveSlice.ToneMode); }
            set
            {
                FMToneMode val = ToneCTCSSToToneMode(value);
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ToneMode = val; }));
            }
        }

        internal float ToneValueToFloat(string val)
        {
            float rv = 0;
            System.Single.TryParse(val, out rv);
            return rv;
        }
        internal string FloatToToneValue(float val)
        {
            return val.ToString("F1");
        }
        public override float ToneFrequency
        {
            get
            {
                return ToneValueToFloat(theRadio.ActiveSlice.FMToneValue);
            }
            set
            {
                string val = FloatToToneValue(value);
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMToneValue = val; }));
            }
        }

        internal OffOnValues FM1750
        {
            get { return (theRadio.ActiveSlice.FMTX1750) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMTX1750 = val; }));
            }
        }

        internal OffOnValues Binaural
        {
            get { return (theRadio.BinauralRX) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.BinauralRX = val; }));
            }
        }

        internal OffOnValues Play
        {
            get { return (theRadio.ActiveSlice.PlayOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.PlayOn = val; }));
            }
        }

        internal OffOnValues Record
        {
            get { return (theRadio.ActiveSlice.RecordOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RecordOn = val; }));
            }
        }

        internal OffOnValues DAXOn
        {
            get { return (theRadio.DAXOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                q.Enqueue((FunctionDel)(() => { theRadio.DAXOn = val; }));
            }
        }

        internal bool CanPlay { get { return theRadio.ActiveSlice.PlayEnabled; } }

        internal const int AMCarrierLevelMin = 0;
        internal const int AMCarrierLevelMax = 100;
        internal const int AMCarrierLevelIncrement = 5;
        internal int AMCarrierLevel
        {
            get { return theRadio.AMCarrierLevel; }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.AMCarrierLevel = value; }));
            }
        }

        internal void setNextValue1()
        {
            if (theRadio.ActiveSlice.DemodMode == "CW")
            {
                theRadio.ActiveSlice.APFOn = !theRadio.ActiveSlice.APFOn;
            }
            else
            {
                theRadio.ActiveSlice.NROn = !theRadio.ActiveSlice.NROn;
            }
        }

        // region remote audio
#region RemoteAudio
        private JJPortaudio.Devices audioSystem;
        private JJPortaudio.Devices.Device remoteInputDevice, remoteOutputDevice;
        private const uint DAXSampleRate = 24000;
        private const uint opusSampleRate = 48000;

        class audioChannelData
        {
            public int ID; // ID in audioChannels.
            public string Name;
            private object radioStream; // the radio's stream
            // OpusStream handles both input and output.
            public OpusStream OpusChannel
            {
                get { return (OpusStream)radioStream; }
                set { radioStream = value; }
            }
            public AudioStream DaxChannel
            {
                get { return (AudioStream)radioStream; }
                set { radioStream = value; }
            }
            // Input stream is for DAX.
            public TXAudioStream InputStream
            {
                get { return (TXAudioStream)radioStream; }
                set { radioStream = value; }
            }
            public bool IsOpus;
            public bool IsInput;
            public JJAudioStream PortAudioStream;
            public bool Started;
            public uint DaxID { get { return DaxChannel.RXStreamID; } }
            public Slice Slice { get { return DaxChannel.Slice; } }
            public audioChannelData(OpusStream stream, string name)
            {
                OpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = false;
            }
            public audioChannelData(AudioStream stream, string name)
            {
                DaxChannel = stream;
                Name = name;
                IsOpus = IsInput = false;
            }
            public audioChannelData(TXAudioStream stream, string name)
            {
                InputStream = stream;
                Name = name;
                IsOpus = false;
                IsInput = true;
            }
        }
        private List<audioChannelData> audioChannels = null; // applies to DAX channels
        private audioChannelData inputChannel; // for DAX input
        private audioChannelData opusChannel; // for output
        private audioChannelData opusInputChannel; // for input
#if CWMonitor
        private Morse CWMon = null;
        private bool useCWMon { get { return (CWMon != null); } }
#endif

        private audioChannelData findAudioChannelByStream(AudioStream stream)
        {
            foreach(audioChannelData chan in audioChannels)
            {
                lock (chan)
                {
                    if (stream.RXStreamID == chan.DaxID) return chan;
                }
            }
            return null;
        }

        private audioChannelData findAudioChannelBySlice(Slice s)
        {
            foreach (audioChannelData chan in audioChannels)
            {
                lock (chan)
                {
                    if (s.DAXChannel == chan.Slice.DAXChannel) return chan;
                }
            }
            return null;
        }

        private void audioStreamAddedHandler(AudioStream stream)
        {
            Tracing.TraceLine("audioStreamAddedHandler:" + stream.DAXChannel, TraceLevel.Info);
            string name = "JJRadio.daxchan" + stream.DAXChannel.ToString();
            audioChannelData chan = new audioChannelData(stream, name);
            chan.ID = audioChannels.Count; // 0-based id
            chan.DaxChannel.RXDataReady += new AudioStream.RXDataReadyEventHandler(audioDataHandler);
            audioChannels.Add(chan);
        }

        private void txAudioStreamAddedHandler(TXAudioStream stream)
        {
            Tracing.TraceLine("txAudioStreamAddedHandler", TraceLevel.Info);
            if (inputChannel == null)
            {
                inputChannel = new audioChannelData(stream, "DAXInput");
            }
            else
            {
                inputChannel.InputStream = stream;
            }
        }

        // For DAX output data.
        private void audioDataHandler(AudioStream stream, float[] data)
        {
            if (!theRadio.DAXOn || stream.Slice.Mute) return;
            audioChannelData chan = findAudioChannelByStream(stream);
            if (chan == null)
            {
                Tracing.TraceLine("audioDataHandler:channel not found", TraceLevel.Error);
                return;
            }
            lock (chan)
            {
                if (chan.Started)
                {
                    chan.PortAudioStream.Write(data);
                }
            }
        }

        private Thread remoteAudioThread;
        private bool stopRemoteAudio;

        public override bool LANAudio
        {
            get { return _LANAudio; }
            set
            {
                Tracing.TraceLine("LANAudio:" + value.ToString(), TraceLevel.Info);
                if (_LANAudio != value)
                {
                    if (value)
                    {
                        startRemoteAudioThread();
                    }
                    else
                    {
                        stopRemoteAudioThread();
                    }
                }
                _LANAudio = value;
            }
        }

        private void startRemoteAudioThread()
        {
            Tracing.TraceLine("startRemoteAudioThread", TraceLevel.Info);
            stopRemoteAudio = false;
            remoteAudioThread = new Thread(remoteAudioProc);
            remoteAudioThread.Name = "RemoteAudio";
            remoteAudioThread.Start();
        }

        private void stopRemoteAudioThread()
        {
            Tracing.TraceLine("stopRemoteAudioThread", TraceLevel.Info);
            stopRemoteAudio = true;
            if (remoteAudioThread != null)
            {
                if (!await(() => { return !remoteAudioThread.IsAlive; }, 1000))
                {
                    Tracing.TraceLine("Remote audio didn't stop", TraceLevel.Error);
                }
            }
        }

        private void remoteAudioProc()
        {
            Tracing.TraceLine("remoteAudioProc useOpus=" + useOpus.ToString() + " isWAN=" + theRadio.IsWan.ToString(), TraceLevel.Info);
            // input is from pc.
            string oldMicInput = theRadio.MicInput;
            theRadio.MicInput = "PC";

            audioSystem = new JJPortaudio.Devices(Callouts.AudioDevicesFile);
            // Get the configured devices.
            if (!audioSystem.Setup())
            {
                Tracing.TraceLine("audio setup failed", TraceLevel.Error);
                goto remoteDone;
            }
            remoteInputDevice =
                audioSystem.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.input, true);
            if (remoteInputDevice == null)
            {
                Tracing.TraceLine("remoteInputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }
            remoteOutputDevice =
                audioSystem.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.output, true);
            if (remoteOutputDevice == null)
            {
                Tracing.TraceLine("remoteOutputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }

            // Start the audio subsystem.
            JJPortaudio.Audio.Initialize(remoteInputDevice, remoteOutputDevice);

            // Setup audio channels
            if (useOpus)
            {
                // output channel
                opusChannel = new audioChannelData(myOpusStream, "JJRadio.opus");
                opusChannel.PortAudioStream = new JJAudioStream();
                if (!opusChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.output, opusSampleRate))
                {
                    Tracing.TraceLine("opus output didn't open", TraceLevel.Error);
                    goto remoteDone;
                }
                // input channel, same stream as output.
                opusInputChannel = new audioChannelData(myOpusStream, "JJRadio.opusInput");
                opusInputChannel.PortAudioStream = new JJAudioStream();
                opusInputChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.input, opusSampleRate, sendOpusInput);
                Tracing.TraceLine("opus channels setup", TraceLevel.Info);
                // start the output now.
                myOpusStream.RemoteRxOn = true;
                opusChannel.PortAudioStream.StartAudio();
                opusChannel.Started = true;
            }
            else
            {
                // Using DAX, one channel per slice.
                audioChannels = new List<audioChannelData>();
                // Setup the receive audio.
                for (int i = 0; i < theRadio.SliceList.Count; i++)
                {
                    int daxChan = i + 1;
                    theRadio.RequestAudioStream(daxChan); // see audioStreamAddedHandler.
                    if (!await(() => { return (audioChannels.Count == i + 1); }, 1000))
                    {
                        Tracing.TraceLine("remoteAudioProc: audio channel not added: " + i, TraceLevel.Error);
                        goto remoteDone;
                    }
                    theRadio.SliceList[i].DAXChannel = daxChan;
                    audioChannelData chan = audioChannels[i];
                    chan.PortAudioStream = new JJAudioStream();
                    chan.PortAudioStream.OpenAudio(Devices.DeviceTypes.output, DAXSampleRate);
                    Tracing.TraceLine("startLocalAudioChannel:" + chan.ID + " setup", TraceLevel.Info);
                }

                theRadio.DAXOn = true;
                for (int i = 0; i < theRadio.SliceList.Count; i++)
                {
                    // Start if not muted.
                    if (!audioChannels[i].Slice.Mute)
                    {
                        startLocalAudioChannel(i);
                    }
                }
                //theRadio.DAXOn = true;

                // Setup the transmit audio
                inputChannel = null;
                theRadio.RequestTXAudioStream(); // see txAudioStreamAddedHandler
                if (await(() => { return (inputChannel != null); }, 1000))
                {
                    inputChannel.PortAudioStream = new JJAudioStream();
                    inputChannel.PortAudioStream.OpenAudio(Devices.DeviceTypes.input, DAXSampleRate, sendAudioInput);
                    Tracing.TraceLine("Local Input Channel setup", TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("remoteAudioProc: didn't get TXAudio stream from radio", TraceLevel.Error);
                }

                //theRadio.DAXOn = true;
            }

#if CWMonitor
            // Also need a cw monitor
            CWMonInit();
#endif

            // Main audio loop.
            // Note that we must pole for opus output.
            while (!stopRemoteAudio)
            {
                if (useOpus)
                {
                    if (Transmit)
                    {
                        startOpusInputChannel(); // only starts it once
                    }
                    else
                    {
                        stopOpusInputChannel(); // only stops it once.
                    }
                    // get opus data, even during transmit (for QSK).
                    bool gotPackets;
                    lock (myOpusStream.OpusRXListLockObj)
                    {
                        int lastID = myOpusStream._opusRXList.Count - 1;
                        // See if have packets to process.
                        gotPackets = ((lastID != -1) && (myOpusStream.LastOpusTimestampConsumed < myOpusStream._opusRXList.Keys[lastID]));
                        if (gotPackets)
                        {
                            // sendPacket is set when need to send packets.
                            bool sendPacket = !myOpusStream._opusRXList.Keys.Contains(myOpusStream.LastOpusTimestampConsumed);
                            // Note that if sendPacket is true, we may have missed packets.
                            if (sendPacket)
                            {
                                Tracing.TraceLine("possible missed packets", TraceLevel.Error);
                            }
                            foreach (KeyValuePair<double, VitaOpusDataPacket> kvp in myOpusStream._opusRXList)
                            {
                                if (sendPacket)
                                {
                                    opusChannel.PortAudioStream.WriteOpus(kvp.Value.payload);
#if opusToFile
                                    writeOpus(kvp.Value.payload);
#endif
                                }
                                // See if need to send subsequent packets.
                                sendPacket = (sendPacket || (kvp.Key == myOpusStream.LastOpusTimestampConsumed));
                            }
                            myOpusStream.LastOpusTimestampConsumed = myOpusStream._opusRXList.Keys[lastID];
                        }
                    }
                    if (!gotPackets & !Transmit)
                    {
                        Thread.Sleep(1);
                    }
                    else Thread.Yield();
                }
                else
                {
                    // using DAX.
                    if (Transmit)
                    {
                        startLocalInputChannel();
                    }
                    else
                    {
                        // receiving
                        stopLocalInputChannel();
                    }
                    Thread.Sleep(10);
                }
            }

            Tracing.TraceLine("stopping remote audio", TraceLevel.Info);

            remoteDone:
#if opusToFile
            closeOpus();
#endif
#if CWMonitor
            if (useCWMon) CWMonDone();
#endif
            if (useOpus & (opusChannel != null))
            {
                myOpusStream.RemoteRxOn = false;
                if (opusChannel.PortAudioStream != null)
                {
                    opusChannel.PortAudioStream.Close();
                }
                // opus channel not closed here.
            }
            if (audioChannels != null)
            {
                theRadio.DAXOn = false;
                for (int i = 0; i < audioChannels.Count; i++)
                {
                    if (audioChannels[i].PortAudioStream != null)
                    {
                        audioChannels[i].PortAudioStream.Close();
                    }
                    if (audioChannels[i].DaxChannel != null)
                    {
                        audioChannels[i].DaxChannel.Close();
                    }
                }
            }
            if (inputChannel != null)
            {
                if (inputChannel.InputStream != null)
                {
                    inputChannel.InputStream.Close();
                }
                if (inputChannel.PortAudioStream != null)
                {
                    inputChannel.PortAudioStream.Close();
                }
            }
            Audio.Terminate();
            // Restore mic input.
            theRadio.MicInput = oldMicInput;

            Tracing.TraceLine("remoteAudioProc exiting", TraceLevel.Info);
        }

        private void sendAudioInput(float[] data)
        {
            if (inputChannel.Started & (data.Length > 0))
            {
                inputChannel.InputStream.AddTXData(data);
            }
        }

        private void sendOpusInput(byte[] data)
        {
            if (opusInputChannel.Started & (data.Length > 0))
            {
                opusInputChannel.OpusChannel.AddTXData(data);
            }
        }

#if opusToFile
        private const string fName = @"c:\users\jjs\documents\tmp\opusOut.dat";
        private Stream fStream = null;
        private BinaryWriter fbw = null;
        private void writeOpus(byte[] buf)
        {
            if (fStream == null)
            {
                fStream = File.Open(fName, FileMode.Create);
                fbw = new BinaryWriter(fStream);
            }
            fbw.Write((ushort)buf.Length);
            fbw.Write(buf, 0, buf.Length);
        }
        private void closeOpus()
        {
            if (fStream != null)
            {
                fbw.Close();
                fbw.Dispose();
                fStream.Dispose();
            }
        }
#endif

        private void startLocalAudioChannel(int id)
        {
            audioChannelData chan = audioChannels[id];
            lock (chan)
            {
                if (chan.Started) return;
                Tracing.TraceLine("startLocalAudioChannel:" + id, TraceLevel.Info);
                chan.PortAudioStream.StartAudio();
                chan.Started = true;
            }
        }

        private void stopLocalAudioChannel(int id)
        {
            Tracing.TraceLine("stopLocalAudioChannel:" + id, TraceLevel.Info);
            audioChannelData chan = audioChannels[id];
            lock (chan)
            {
                if (!chan.Started) return;
                chan.Started = false;
            }
            chan.PortAudioStream.StopAudio();
        }

        private void startLocalInputChannel()
        {
            if (inputChannel.Started) return;
            Tracing.TraceLine("startLocalInputChannel", TraceLevel.Info);
            inputChannel.InputStream.TXGain = 50; // tbd
            inputChannel.PortAudioStream.StartAudio();
            inputChannel.InputStream.Transmit = true;
            inputChannel.Started = true;
        }

        private void stopLocalInputChannel()
        {
            if (!inputChannel.Started) return;
            Tracing.TraceLine("stopLocalInputChannel", TraceLevel.Info);
            inputChannel.PortAudioStream.StopAudio();
            inputChannel.Started = false;
            inputChannel.InputStream.Transmit = false;
        }

        private void startOpusInputChannel()
        {
            if (opusInputChannel.Started) return;
            Tracing.TraceLine("startOpusInputChannel", TraceLevel.Info);
            opusInputChannel.PortAudioStream.StartAudio();
            opusInputChannel.Started = true;
        }

        private void stopOpusInputChannel()
        {
            if (!opusInputChannel.Started) return;
            Tracing.TraceLine("stopOpusInputChannel", TraceLevel.Info);
            opusInputChannel.Started = false;
            opusInputChannel.PortAudioStream.StopAudio();
        }

#if CWMonitor
        // Remote CW monitor
        private void CWMonInit()
        {
            Tracing.TraceLine("CWMonInit", TraceLevel.Info);
            CWMon = new Morse();
            CWMon.Speed = (uint)theRadio.CWSpeed;
            CWMon.Frequency = (uint)theRadio.CWPitch;
            if ((theRadio.ActiveSlice == VFOToSlice(TXVFO)) && (theRadio.ActiveSlice.DemodMode == "CW"))
            {
                CWMonStart();
            }
        }

        // The monitor is started and stopped when we go in or out of transmit.
        private void CWMonStart()
        {
            Tracing.TraceLine("CWMonStart", TraceLevel.Info);
            CWMon.Start();
            CWMon.Frequency = (uint)theRadio.CWPitch;
            CWMon.Speed = (uint)theRadio.CWSpeed;
            CWMon.Volume = theRadio.TXCWMonitorGain;
        }

        private void CWMonStop()
        {
            Tracing.TraceLine("CWMonStop", TraceLevel.Info);
            CWMon.Stop();
        }

        private void CWMonDone()
        {
            Tracing.TraceLine("CWMonDone", TraceLevel.Info);
            CWMonStop();
            CWMon.Close();
        }
#endif
#endregion

        // region - cw
#region cw
        class cwText
        {
            public string Text;
            public bool Stop;
            public cwText()
            {
                Stop = true;
            }
            public cwText(string str)
            {
                Text = str;
            }
        }
        public override bool SendCW(string str)
        {
            q.Enqueue(new cwText(str));
            return true;
        }
        public override void StopCW()
        {
            q.Enqueue(new cwText());
        }

        protected Flex6300Filters FilterObj;
        public override void CWZeroBeat()
        {
            ulong freq = 0;
            if (FilterObj != null) freq = FilterObj.ZeroBeatFreq();
            Tracing.TraceLine("CWZeroBeatFreq:" + freq.ToString(), TraceLevel.Info);
            if (freq != 0)
            {
                RITData r = RIT;
                if (r.Active)
                {
                    r.Value = (int)((long)freq - (long)RXFrequency);
                    RIT = r;
                }
                else RXFrequency = freq;
            }
        }
#endregion

        internal class q_t
        {
            private Queue q;
            public q_t()
            {
                q = Queue.Synchronized(new Queue());
            }

            public bool MainLoop { get; set; }

            public int Count { get { return q.Count; } }

            public void Enqueue(object o, bool beforeMainLoop = false)
            {
                if (!MainLoop)
                {
                    // If can execute before the main loop, do it.
                    if (beforeMainLoop & (o is FunctionDel))
                    {
                        FunctionDel func = (FunctionDel)o;
                        if (func != null) func();
                    }
                    else Tracing.TraceLine("q:outside main loop", TraceLevel.Error);
                }
                else
                {
                    q.Enqueue(o);
                }
            }

            public object Dequeue()
            {
                return q.Dequeue();
            }
        }
        internal q_t q;

        public Flex()
        {
            Tracing.TraceLine("Flex constructor", TraceLevel.Info);
            // Setup the mode table.
            ModeValue[] myModeTable = new ModeValue[modeDictionary.Count];
            int i = 0;
            foreach (ModeValue m in modeDictionary.Values)
            {
                myModeTable[i++] = m;
            }
            ModeTable = myModeTable;

            FMToneModes = myFMToneModes;
            // Use the TS590 fm tone values.
            ToneFrequencyTable = KenwoodTS590.myToneFrequencyTable;

            q = new q_t();
        }

        public const string JJRadioDefault = "JJRadioDefault";
        private Thread mainThread;
        /// <summary>
        /// Open the radio
        /// </summary>
        /// <returns>True on success </returns>
        public override bool Open(OpenParms p)
        {
            Tracing.TraceLine("Flex Open", TraceLevel.Info);
            NetworkRadio = true;
            p.RigDoesPanning = true;
            p.NextValue1 = setNextValue1;
            p.GetSWRText = SWRText;

            bool rv = base.Open(p); // Sets Callouts.
            if (rv)
            {
                mainThread = new Thread(mainThreadProc);
                mainThread.Name = "mainThread";
                mainThread.Start(Callouts);
                Thread.Sleep(0);
            }

            // Radio isn't started yet, see NetOpenEvent.
            IsOpen = rv;
            return rv;
        }

        internal bool Closing = false;
        public override void close()
        {
            Tracing.TraceLine("flex close",TraceLevel.Info);
            Closing = true;
            if ((mainThread != null) && mainThread.IsAlive)
            {
                // Maybe still discovering.
                // Stop the main thread.
                Tracing.TraceLine("flex close:stopping main thread", TraceLevel.Info);
                stopMainThread = true;
                if (await(() => { return !mainThread.IsAlive; }, 30000))
                {
                    Tracing.TraceLine("flex close:main thread stopped", TraceLevel.Info);
                }
                else Tracing.TraceLine("flex close:main thread didn't stop", TraceLevel.Info);
                stopMainThread = false;
            }
            mainThread = null;

            if (theRadio != null)
            {
                _Reconnect = false; // Don't reconnect!
                if (RemoteRig)
                {
                    if (useOpus)
                    {
                        stopRemoteAudioThread();
                        myOpusStream.RemoteRxOn = false;
                        myOpusStream.Close();
                        myOpusStream = null;
                    }
                    theRadio.Disconnect();
                    wan.Disconnect();
                }
                else theRadio.Disconnect();
            }
            API.CloseSession();

            base.close(); // resets IsOpen.
        }

        /// <summary>
        /// mode dictionary
        /// </summary>
        internal static Dictionary<string, ModeValue> modeDictionary = new Dictionary<string, ModeValue>()
        { { "none", new ModeValue(0, '0',"none") },
          { "LSB", new ModeValue(1, '1', "lsb") },
          { "USB", new ModeValue(2, '2', "usb") },
          { "CW", new ModeValue(3, '3', "cw") },
          { "FM", new ModeValue(4, '4', "fm") },
          { "AM", new ModeValue(5, '5', "am") },
          { "DIGL", new ModeValue(6, '6', "digl") },
          { "DIGU", new ModeValue(7, '7', "digu") },
          { "NFM", new ModeValue(8, '8', "NFM") },
          { "DFM", new ModeValue(9, '9', "DFM") },
          { "SAM", new ModeValue(11, (char)0x3A, "SAM") },
        };
        /// <summary>
        /// Flex demodmode (string) to internal mode.
        /// </summary>
        /// <param name="m">the demodmode string</param>
        /// <returns>modes value</returns>
        internal ModeValue getMode(string m)
        {
            Tracing.TraceLine("getMode:" + m, TraceLevel.Verbose);
            ModeValue rv;
            try
            {
                rv = modeDictionary[m];
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("getMode:" + ex.Message, TraceLevel.Error);
                rv = modeDictionary["none"];
            }
            return rv;
        }
        /// <summary>
        /// Get demodmode string to send to the rig.
        /// </summary>
        /// <param name="m">modeValue item</param>
        /// <returns>demodmode</returns>
        internal string FlexMode(ModeValue m)
        {
            Tracing.TraceLine("FlexMode:" + m.ToString(), TraceLevel.Verbose);
            string rv = "USB";
            foreach (ModeValue mv in modeDictionary.Values)
            {
                if (m == mv)
                {
                    rv = mv.ToString();
                    break;
                }
            }
            return rv;
        }

        // main thread region
#region mainThread
        private bool stopMainThread;
        internal delegate void FunctionDel();
        private bool _Reconnect = false;

        // Experimental code
#if zero
        private int _ticker = 0;
        private void ticker(object state)
        {
            Tracing.TraceLine("Ticker:" + _ticker);
            _ticker++;
        }
#endif

        private void mainThreadProc(object o)
        {
            try
            {
                OpenParms p = (OpenParms)o;
                Tracing.TraceLine("flex open:" + p.NetworkRadio.Serial.ToString(), TraceLevel.Info);
                bool rv;

                if (p.AllowRemote)
                {
                    // Discover radios.  Check the WAN if none on local net.
                    theRadio = DiscoverFlexRadios(p.NetworkRadio, true);
                }
                else
                {
                    // Not allowing remote access.
                    while ((theRadio == null) & !stopMainThread)
                    {
                        theRadio = DiscoverFlexRadios(p.NetworkRadio, false);
                    }
                }
                rv = (theRadio != null);
                if (!rv)
                {
                    if (stopMainThread) Tracing.TraceLine("flex open discovery interrupted", TraceLevel.Error);
                    else Tracing.TraceLine("Flex Open - radio not found", TraceLevel.Error);
                    goto flexOpenDone;
                }

                RemoteRig = theRadio.IsWan;

                // Now add the handlers.
                theRadio.PropertyChanged += new PropertyChangedEventHandler(propertyChangedHandler);
                theRadio.MessageReceived += new Radio.MessageReceivedEventHandler(messageReceivedHandler);
                theRadio.SliceAdded += new Radio.SliceAddedEventHandler(sliceAdded);
                theRadio.SliceRemoved += new Radio.SliceRemovedEventHandler(sliceRemoved);
                theRadio.PanadapterAdded += new Radio.PanadapterAddedEventHandler(panadapterAdded);
                theRadio.PanadapterRemoved += new Radio.PanadapterRemovedEventHandler(panAdapterRemoved);
                theRadio.WaterfallRemoved += new Radio.WaterfallRemovedEventHandler(waterfallRemoved);
                theRadio.TNFAdded += new Radio.TNFAddedEventHandler(tnfAdded);
                theRadio.TNFRemoved += new Radio.TNFRemovedEventHandler(tnfRemoved);
                theRadio.IsTNFSubscribed = true; // v2.0.19
                theRadio.TNFEnabled = true;
                theRadio.ForwardPowerDataReady += new Radio.MeterDataReadyEventHandler(forwardPowerData);
                theRadio.SWRDataReady += new Radio.MeterDataReadyEventHandler(sWRData);
                theRadio.MicDataReady += new Radio.MeterDataReadyEventHandler(micData);
                theRadio.MicPeakDataReady += new Radio.MeterDataReadyEventHandler(micPeakData);
                theRadio.CompPeakDataReady += new Radio.MeterDataReadyEventHandler(compPeakData);
                theRadio.HWAlcDataReady += new Radio.MeterDataReadyEventHandler(hwALCData);
                theRadio.AudioStreamAdded += new Radio.AudioStreamAddedEventHandler(audioStreamAddedHandler);
                theRadio.TXAudioStreamAdded += new Radio.TXAudioStreamAddedEventHandler(txAudioStreamAddedHandler);
                theRadio.OpusStreamAdded += new Radio.OpusStreamAddedEventHandler(opusStreamAddedHandler);

                // Connect
                rv = theRadio.Connect();
                //rv = await(() => { return (theRadio.Connected); }, 1000);
                if (!rv)
                {
                    Tracing.TraceLine("flex open radio didn't connect", TraceLevel.Error);
                    goto flexOpenDone;
                }
                Tracing.TraceLine("flex open:connected", TraceLevel.Info);

                // Experimental code
                //System.Threading.Timer timeTicker = new System.Threading.Timer(ticker, null, 100, 100);
                //while (true) { Thread.Yield(); }

                // If JJRadioDefault was loaded, select it and await the pan and slices.
                if (GetProfileInfo(false))
                {
                    Tracing.TraceLine("flex open:got info from profile", TraceLevel.Info);
                    goto flexOpenDone;
                }

                // Radio was reset or never used with JJRadio before this.
                Tracing.TraceLine("flex open:didn't find " + JJRadioDefault, TraceLevel.Info);
                // Get pan adapters.
                //while (!await(() => { return (panCount == (myCaps.MaxVFO + 1)); }, 2000))
                while(theRadio.PanadaptersRemaining > 0) 
                {
                    theRadio.RequestPanafall();
                    Thread.Sleep(100);
                }
                //if (await(() => { return (theRadio.SliceList.Count == (myCaps.MaxVFO + 1)); }, 5000))
                if (await(() =>
                {
                    return pansAndSlicesPresent;
                }, 5000))
                {
                    // We have pan adapters and slices, so we're done.
                    myCaps.MaxVFO = theRadio.SliceList.Count - 1;
                    Tracing.TraceLine("flex open radio have pan and slices:" + theRadio.PanadapterList.Count, TraceLevel.Info);
                    VFOToSlice(RigCaps.VFOs.VFOA).Active = true;
                    // Wait until other slices are inactive.
                    for(int i = 1; i <= myCaps.MaxVFO; i++)
                    {
                        Slice s = theRadio.SliceList[i];
                        bool sw = await(() => { return !s.Active; }, 1000);
                        if (!sw) Tracing.TraceLine("Flex open:slice:" + i + " didn't deactivate", TraceLevel.Error);
                    }
                    Tracing.TraceLine("Flex open:only VFO A should be active", TraceLevel.Info);
                    VFOToSlice(RigCaps.VFOs.VFOA).IsTransmitSlice = true;
                    if (await(() => { return ((theRadio.RXAntList != null) && (theRadio.RXAntList.Length > 0)); }, 2000))
                    {
                        VFOToSlice(RXVFO).TXAnt = theRadio.RXAntList[0];
                        VFOToSlice(TXVFO).TXAnt = theRadio.RXAntList[0];
                    }
                    foreach (Slice s in theRadio.SliceList)
                    {
                        s.Mute = true;
                    }
                    theRadio.SliceList[0].Mute = false;
                    theRadio.RFPower = 100;
                    theRadio.CWBreakIn = false;
                    theRadio.CWIambic = false;
                    theRadio.SpeechProcessorEnable = true;
                    theRadio.SimpleVOXEnable = false;
                    Tracing.TraceLine("flex open radio setup", TraceLevel.Info);
                }
                flexOpenDone:
                if (rv)
                {
                    Tracing.TraceLine("flex open:setup:" + theRadio.PanadapterList.Count + " panadapters", TraceLevel.Info);

                    // Set these on every open.
                    theRadio.MicInput = "mic";

                    foreach (Slice s in theRadio.SliceList)
                    {
                        if (s.IsTransmitSlice)
                        {
                            _TXVFO = SliceToVFO(s);
                            _TXFrequency = LibFreqtoLong(s.Freq);
                            break;
                        }
                    }
                    //theRadio.PanadapterList[0].AutoCenter = false;
                    VFOToSlice(RigCaps.VFOs.VFOA).AutoPan = false;
                    VFOToSlice(RigCaps.VFOs.VFOB).AutoPan = false;

                    // Ok to queue commands now.
                    q.MainLoop = true;

                    cwx = theRadio.GetCWX();
                    cwx.Delay = theRadio.CWDelay;
                    cwx.Speed = theRadio.CWSpeed;
                    cwx.CharSent += new CWX.CharSentEventHandler(charSentHandler);

                    // Setup pan adapter display.
                    if (PanSetup != null)
                    {
                        PanSetup();
                        RXFreqChange(theRadio.ActiveSlice);
                    }

                    if (RemoteRig)
                    {
                        await(() => { return useOpus; }, 1000);
                        if (useOpus) startRemoteAudioThread();
                        else Tracing.TraceLine("no opus stream for remote rig", TraceLevel.Error);
                    }
                    else
                    {
                        if (myOpusStream != null)
                        {
                            myOpusStream.RemoteRxOn = false;
                            //myOpusStream.Close();
                            myOpusStream = null;
                        }
                    }

#if zero
                    // Pan RF markers
                    foreach (Panadapter pa in theRadio.PanadapterList)
                    {
                        pa.RFGainStep = 5;
                        pa.RFGainLow = 0;
                        pa.RFGainHigh = 20;
                    }
#endif

                    raisePowerOn();

                    // Indicate memories loaded.
                    RefreshMemories(); // Don't raise event yet.
                    MemoriesLoaded = true;

                    // Main loop.
                    _Reconnect = true;
                    while (!stopMainThread)
                    {
                        while (q.Count > 0)
                        {
                            object el = q.Dequeue();
                            if (el is FunctionDel)
                            {
                                FunctionDel func = (FunctionDel)el;
                                if (func != null) func();
                            }
                            else if (el is cwText)
                            {
                                cwText cwt = (cwText)el;
                                if (cwt.Stop) stopCW();
                                else sendText(cwt.Text);
                            }
                        }

                        Thread.Sleep(25);
                        //Thread.Yield();
                    }
                    q.MainLoop = false;
                    _Reconnect = false;

                    SaveProfile(true);
                }
            }
            catch (ThreadAbortException) { Tracing.TraceLine("mainThread abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex, true);
            }
        }

        private void reconnect()
        {
            Tracing.TraceLine("reconnect", TraceLevel.Error);
            if (await(() => { return (theRadio.Status == "Available"); }, 2000))
            {
                Tracing.TraceLine("reconnect Available", TraceLevel.Error);
                theRadio.Connect();
            }
        }

        internal void SaveProfile(bool delay)
        {
            theRadio.SaveTXProfile(JJRadioDefault);
            theRadio.SaveGlobalProfile(JJRadioDefault);
            if (delay) Thread.Sleep(1000);
            Tracing.TraceLine("JJRadioDefault profile saved", TraceLevel.Info);
        }

        private bool pansAndSlicesPresent
        {
            get
            {
                return ((theRadio.PanadaptersRemaining == 0) &&
                    (theRadio.PanadapterList.Count == theRadio.SliceList.Count));
            }
        }

        /// <summary>
        /// Select the default profile if loaded.
        /// Before calling, call RaisePowerOff(), and PowerOn() when ready afterwards.
        /// </summary>
        /// <returns>true if selected and the info is loaded.</returns>
        /// <remarks>
        /// - At program start, we'll wait for any initial activity, then select the profile.
        /// - After an import, the initial wait just falls through.
        /// </remarks>
        internal bool GetProfileInfo(bool postImport)
        {
            #region copy
            Tracing.TraceLine("getProfileInfo", TraceLevel.Info);
            bool rv = false;

            // Must be available after an import also.
            Tracing.TraceLine("getProfileInfo:awaiting inUse", TraceLevel.Info);
            if (!await(() =>
            {
                return (theRadio.Status == "In_Use");
            }, 3000))
            {
                Tracing.TraceLine("getProfileInfo:not inUse", TraceLevel.Error);
            }

            // Await to see if JJRadioDefault is in the profile list.
            Tracing.TraceLine("getProfileInfo:awaiting JJRadioDefault in GlobalProfileList", TraceLevel.Info);
            if (!(rv = await(() =>
            {
                return theRadio.ProfileGlobalList.Contains(JJRadioDefault);
            }, 3000)))
            {
                Tracing.TraceLine("GetProfileInfo:JJRadioDefault not present", TraceLevel.Error);
                return rv;
            }

            // This is some weird stuff, but it works!
            waterfallList = null;
            JJRadioDefaultSelected = false;
            Tracing.TraceLine("getProfileInfo:point 0", TraceLevel.Info);
            if (!await(() =>
            {
                return pansAndSlicesPresent;
            }, 1000))
            {
                Tracing.TraceLine("getProfileInfo:point 1", TraceLevel.Info);
                // Set JJRadioDefault as the current profile.
                theRadio.ProfileGlobalSelection = JJRadioDefault;
                Tracing.TraceLine("getProfileInfo:point 2", TraceLevel.Info);
                if ((rv = await(() =>
                {
                    return pansAndSlicesPresent;
                }, 3000)))
                {
                    Tracing.TraceLine("getProfileInfo:point 3", TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("getProfileInfo:point 4", TraceLevel.Error);
                }
            }
            else
            {
                Tracing.TraceLine("getProfileInfo:point 5", TraceLevel.Info);
                theRadio.ProfileGlobalSelection = JJRadioDefault;
                Tracing.TraceLine("getProfileInfo:point 6", TraceLevel.Info);
                if (await(() =>
                {
                    return ((theRadio.PanadapterList.Count == 0) &
                        (theRadio.SliceList.Count == 0));
                }, 3000, 5))
                {
                    Tracing.TraceLine("getProfileInfo:point 7", TraceLevel.Info);
                    if (await(() =>
                    {
                        return pansAndSlicesPresent;
                    }, 3000))
                    {
                        Tracing.TraceLine("getProfileInfo:point 8", TraceLevel.Info);
                    }
                    else
                    {
                        Tracing.TraceLine("getProfileInfo:point 9", TraceLevel.Error);
                    }
                }
                else
                {
                    Tracing.TraceLine("getProfileInfo:point 10", TraceLevel.Error);
                }
            }

            if (RXFreqChange != null) RXFreqChange(theRadio.ActiveSlice);
            if (postImport)
            {
                Tracing.TraceLine("flex import operation complete:" + rv.ToString(), TraceLevel.Info);
                raisePowerOn();
                Directory.Delete(importDir, true);
                string msg = (rv) ? importedMsg : importFailMsg;
                MessageBox.Show(msg, statusHdr, MessageBoxButtons.OK);
            }
            return rv;
            #endregion
        }

        private string importDir;
        internal void ImportProfile(string name)
        {
            // Save the import temp directory.
            importDir = name.Substring(0, name.LastIndexOf('\\'));
            raisePowerOff();
            theRadio.DatabaseImportComplete = false;
            theRadio.SendDBImportFile(name);
#if zero
            if (await(() => { return theRadio.DatabaseImportComplete; }, 30000))
            {
                Tracing.TraceLine("flex import operation complete", TraceLevel.Info);
                // Now, select the profile.
                GetProfileInfo();
                MessageBox.Show(importedMsg, statusHdr, MessageBoxButtons.OK);
            }
            else
            {
                Tracing.TraceLine("flex import operation didn't complete", TraceLevel.Info);
                MessageBox.Show(importFailMsg, statusHdr, MessageBoxButtons.OK);
            }
            raisePowerOn();
#endif
        }

        private CWX cwx;
        private void sendText(string str)
        {
            if (string.IsNullOrEmpty(str) || (Vox == OffOnValues.off)) return;

            if (str[str.Length - 1] != ' ') str += " ";
            cwx.Send(str);
            sentChars.Append(str);
            Tracing.TraceLine("SendCW:" + str, TraceLevel.Info);
        }
        private void stopCW()
        {
            cwx.ClearBuffer();
        }
#endregion

        // region - Memory stuff
#region memories
        internal class memoryElement
        {
            private Memory memory;
            public memoryElement(Memory m)
            {
                memory = m;
            }
            // Used by the memory form.
            public string Display
            {
                get { return flexMemName(memory); }
            }
            public Memory Value
            {
                get { return memory; }
            }
        }

        internal override string DisplayMemName(MemoryData mem)
        {
            return flexMemName((Memory)mem.ExternalMemory);
        }
        private static string flexMemName(Memory mem)
        {
            return (string.IsNullOrEmpty(mem.Name)) ? mem.Freq.ToString("F6") : mem.Name;
        }

        /// <summary>
        /// Sort memory elements by group/name and/or frequency.
        /// </summary>
        /// <returns>sorted list of memoryElements</returns>
        internal List<memoryElement> SortElements()
        {
            List<Flex.memoryElement> sortedMemories = new List<Flex.memoryElement>();
            if (theRadio.MemoryList.Count > 0)
            {
                foreach (Memory m in theRadio.MemoryList)
                {
                    // Don't include a null memory.
                    if ((m.Freq==0) | (m.Mode == null))
                    {
                        Tracing.TraceLine("SortElements:null element", TraceLevel.Error);
                        continue;
                    }
                    sortedMemories.Add(new Flex.memoryElement(m));
                }
                sortedMemories.Sort(compareMemoryElements);
            }
            return sortedMemories;
        }
        private int compareMemoryElements(memoryElement x, memoryElement y)
        {
            int rv;
            // If they have groups/names, compare those.
            string xstr = sortName(x);
            string ystr = sortName(y);
            if ((xstr != "") & (ystr != ""))
            {
                int len = Math.Min(xstr.Length, ystr.Length);
                rv = string.Compare(xstr.Substring(0, len), ystr.Substring(0, len));
                // if initial portions are equal, favor the shorter one.
                if (rv == 0)
                {
                    rv = xstr.Length.CompareTo(ystr.Length);
                }
            }
            // If neither has a name, compare frequencies.
            else if ((xstr == "") & (ystr == ""))
            {
                rv = x.Value.Freq.CompareTo(y.Value.Freq);
            }
            // favor name over frequency
            else if (xstr == "") rv = 1;
            else rv = -1;
            return rv;
        }
        private string sortName(memoryElement el)
        {
            string rv = "";
            if (!string.IsNullOrEmpty(el.Value.Group)) rv += el.Value.Group;
            if (!string.IsNullOrEmpty(el.Value.Name)) rv += el.Value.Name;
            return rv;
        }

        /// <summary>
        /// Setup the Memories object from the Flex memories.
        /// </summary>
        internal void RefreshMemories()
        {
            if ((theRadio.MemoryList == null) || (theRadio.MemoryList.Count == 0))
            {
                Memories = null;
                return;
            }

            List<memoryElement> sorted = SortElements();
            Memories = new MemoryGroup(sorted.Count, this);
            for (int i = 0; i < sorted.Count; i++)
            {
                Memories.mems[i].Present = true;
                Memories.mems[i].ExternalMemory = sorted[i].Value;
            }
        }

#if FlexGroups
        // Flex allows user-defined memory groups.
        public override List<ScanGroup> GetReservedGroups()
        {
            if ((theRadio.MemoryList == null) || (theRadio.MemoryList.Count == 0)) return null;
            Tracing.TraceLine("GetReservedGroups", TraceLevel.Info);
            Dictionary<string, List<MemoryData>> groups = new Dictionary<string, List<MemoryData>>();
            for (int i = 0; i < Memories.mems.Length; i++)
            {
                Memory mem = (Memory)Memories.mems[i].ExternalMemory;
                List<MemoryData> val = null;
                if (!groups.TryGetValue(mem.Group, out val))
                {
                    // New group
                    val = new List<MemoryData>();
                    groups.Add(mem.Group, val);
                }
                val.Add(Memories.mems[i]);
            }
            if (groups.Keys.Count == 0) return null;
            List<ScanGroup> rv = new List<ScanGroup>();
            foreach (string key in groups.Keys)
            {
                rv.Add(new ScanGroup(key, Memories.Bank, groups[key], true));
            }
            Tracing.TraceLine("GetReservedGroups:" + rv.Count, TraceLevel.Info);
            return rv;
        }
#endif

        public override int CurrentMemoryChannel
        {
            get { return base.CurrentMemoryChannel; }
            set
            {
                if ((value < NumberOfMemories) & (value != CurrentMemoryChannel))
                {
                    _CurrentMemoryChannel = value;
                    if (MemoryMode)
                    {
                        q.Enqueue((FunctionDel)(() => { ((Memory)Memories.mems[value].ExternalMemory).Select(); }));
                    }
                }
            }
        }

        private bool _MemoryMode;
        public override bool MemoryMode
        {
            get { return (Memories == null)? false: _MemoryMode; }
            set
            {
                if (MemoryMode != value)
                {
                    _MemoryMode = value;
                    if (value)
                    {
                        // go to the memory.
                        q.Enqueue((FunctionDel)(() => { ((Memory)Memories.mems[CurrentMemoryChannel].ExternalMemory).Select(); }));
                    }
                }
            }
        }
        public override bool IsMemoryMode(RigCaps.VFOs v)
        {
            return _MemoryMode;
        }

        public override bool MemoryToVFO(int n, RigCaps.VFOs vfo)
        {
            if (!MemoryMode || (vfo != CurVFO)) return false;
            // We know this vfo is at the memory.
            MemoryMode = false;
            return true;
        }

        internal override bool getMem(AllRadios.MemoryData m)
        {
            return ((m != null) && (m.Number < NumberOfMemories));
        }
#endregion
    }
}
