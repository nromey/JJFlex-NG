//#define KeepAlive
//#define feedback // for testing mic input
//#define opusToFile
//#define opusInputToFile
//#define TwoSlices
//#define NoATU
#define CWMonitor
using System;
using System.Collections;
using System.Collections.Concurrent;
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
using System.Xml.Serialization;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using HamBands;
using JJPortaudio;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Flex superclass
    /// </summary>
    public class FlexBase : AllRadios, IDisposable
    {
        private const string statusHdr = "Status";
        private const string importedMsg = "Import complete";
        private const string importFailMsg = "import didn't complete";
        private const string noRXAnt = "no RX antenna";
        private const string noSlice = "didn't get a slice";
        private const string noStation = "Station name not set";

        /// <summary>
        /// Data describing a rig.
        /// </summary>
        public class RigData
        {
            public string Name;
            public string ModelName;
            public string Serial;
            public bool Remote { get; internal set; }
            internal RigData() { }
        }
        public delegate void RadioFoundDel(object sender, RigData r);
        /// <summary>
        /// Radio found event, local or remote.
        /// </summary>
        public static event RadioFoundDel RadioFound;
        /// <summary>
        /// Raise RadioFound.
        /// </summary>
        /// <param name="sender">sending object, or null.</param>
        /// <param name="r">Radio object</param>
        internal static void RaiseRadioFound(object sender, RigData r)
        {
            if (RadioFound != null)
            {
                Tracing.TraceLine("RaiseRadioFound:" + r.Serial, TraceLevel.Info);
                RadioFound(sender, r);
            }
        }

        private List<Radio> myRadioList = new List<Radio>();
        private void radioAddedHandler(Radio r)
        {
            Tracing.TraceLine("radioAddedHandler:" + r.Serial, TraceLevel.Info);
            // Ignore entries without a serial; SmartLink returns unusable shells before auth completes.
            if (string.IsNullOrWhiteSpace(r.Serial))
            {
                Tracing.TraceLine("radioAddedHandler: ignored radio with empty serial", TraceLevel.Warning);
                return;
            }
            myRadioList.Add(r);
            RigData rd = new RigData();
            rd.Name = string.IsNullOrWhiteSpace(r.Nickname) ? "Unknown" : r.Nickname;
            rd.ModelName = string.IsNullOrWhiteSpace(r.Model) ? "Unknown" : r.Model;
            rd.Serial = r.Serial;
            rd.Remote = r.IsWan;
            RaiseRadioFound(null, rd);
        }
        internal static bool _apiInit;
        internal void apiInit(bool force = false)
        {
            Tracing.TraceLine("apiInit:" + force.ToString(), TraceLevel.Info);
            if (force)
            {
                // Always initialize.
                if (_apiInit)
                {
                    API.CloseSession();
                    // Force init.
                    _apiInit = false;
                }
            }
            // Won't init if !force and already inited.
            if (!_apiInit)
            {
                API.RadioAdded -= radioAddedHandler;
                API.RadioAdded += radioAddedHandler;
                API.Init();
                _apiInit = true;
            }
        }

        private Radio findRadioInAPI(string serial)
        {
            foreach (Radio r in myRadioList)
            {
                if (r.Serial == serial) return r;
            }
            return null;
        }

        /// <summary>
        /// Provide a list of local radios through the RadioFound event.
        /// </summary>
        public void LocalRadios()
        {
            Tracing.TraceLine("LocalRadios", TraceLevel.Info);
            apiInit(true);
        }

        /// <summary>
        /// Provide a list of remote radios through the RadioFound event.
        /// </summary>
        public void RemoteRadios()
        {
            Tracing.TraceLine("RemoteRadios", TraceLevel.Info);
            apiInit(); // don't force the init.
            bool stat = setupRemote();
            Tracing.TraceLine("RemoteRadios setupRemote:" + stat.ToString(), TraceLevel.Info);
        }

        internal Radio theRadio;
        private FeatureLicense trackedFeatureLicense;
        public event EventHandler FeatureLicenseChanged;
        public string RadioModel => theRadio?.Model ?? string.Empty;
        /// <summary>
        /// Gets the connected radio's nickname (user-assigned name), or empty if not connected.
        /// </summary>
        public string RadioNickname => theRadio?.Nickname ?? string.Empty;
        public bool NoiseReductionLicenseReported => theRadio?.FeatureLicense?.LicenseFeatNoiseReduction != null;
        public bool NoiseReductionLicensed => theRadio?.FeatureLicense?.LicenseFeatNoiseReduction?.FeatureEnabled == true;
        public bool DiversityLicenseReported => theRadio?.FeatureLicense?.LicenseFeatDivEsc != null;
        public bool DiversityLicensed => theRadio?.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true;
        public bool DiversityHardwareSupported => theRadio?.DiversityIsAllowed == true;
        private Thread mainThread;
        /// <summary>
        /// Connect to the specified radio.
        /// </summary>
        /// <param name="serial">serial#</param>
        /// <param name="lowBW">true if low bandwidth connect</param>
        public bool Connect(string serial, bool lowBW)
        {
            Tracing.TraceLine("Connect:" + serial, TraceLevel.Info);
            bool rv = true;

            theRadio = findRadioInAPI(serial);
            if (theRadio == null)
            {
                Tracing.TraceLine("Connect didn't find radio", TraceLevel.Error);
                return false;
            }

            // add the handlers.
            theRadio.PropertyChanged += new PropertyChangedEventHandler(radioPropertyChangedHandler);
            theRadio.MessageReceived += new Radio.MessageReceivedEventHandler(messageReceivedHandler);
            theRadio.GUIClientAdded += new Radio.GUIClientAddedEventHandler(guiClientAdded);
            theRadio.GUIClientUpdated += new Radio.GUIClientUpdatedEventHandler(guiClientUpdated);
            theRadio.GUIClientRemoved += new Radio.GUIClientRemovedEventHandler(guiClientRemoved);
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
            theRadio.PATempDataReady += new Radio.MeterDataReadyEventHandler(PATempDataHandler);
            theRadio.VoltsDataReady += new Radio.MeterDataReadyEventHandler(VoltsDataHandler);
            theRadio.HWAlcDataReady += new Radio.MeterDataReadyEventHandler(hwALCData);
            theRadio.TxBandSettingsAdded += new Radio.TxBandSettingsAddedEventHandler(txBandSettingsHandler);
            theRadio.RXRemoteAudioStreamAdded += new Radio.RXRemoteAudioStreamAddedEventHandler(opusOutputStreamAddedHandler);
            theRadio.TXRemoteAudioStreamAdded += new Radio.TXRemoteAudioStreamAddedEventHandler(opusInputStreamAddedHandler);
            HookFeatureLicense(theRadio);

            theRadio.LowBandwidthConnect = lowBW;

            if (RemoteRig)
            {
                rv = sendRemoteConnect(theRadio);
            }

            if (rv)
            {
                rv = theRadio.Connect();
            }

            if (rv)
            {
                Tracing.TraceLine("Connect worked:" + theRadio.Serial, TraceLevel.Info);

                if (RemoteRig)
                {
                    //PCAudio = true;
                }
                else
                {
                    // local audio on
                    //LocalAudioMute(false);
                    theRadio.IsMuteLocalAudioWhenRemoteOn = false; ;
                }
            }
            else
            {
                Tracing.TraceLine("Connect failed", TraceLevel.Error);
            }

            return rv;
        }

        /// <summary>
        /// Attempts to auto-connect to a radio based on saved configuration.
        /// For local radios: starts discovery, waits for radio, connects.
        /// For remote radios: authenticates silently, waits for radio, connects.
        /// </summary>
        /// <param name="config">Auto-connect configuration</param>
        /// <param name="timeoutMs">How long to wait for radio discovery (default 10 seconds)</param>
        /// <returns>True if connection succeeded, false otherwise</returns>
        public bool TryAutoConnect(AutoConnectConfig config, int timeoutMs = 10000)
        {
            if (config == null || !config.ShouldAutoConnect)
            {
                Tracing.TraceLine("TryAutoConnect: no config or not enabled", TraceLevel.Info);
                return false;
            }

            Tracing.TraceLine($"TryAutoConnect: {config.RadioName} ({config.RadioSerial}), remote={config.IsRemote}", TraceLevel.Info);
            ScreenReaderOutput.Speak($"Connecting to {config.RadioName}", true);

            try
            {
                if (config.IsRemote)
                {
                    // Remote radio - need to authenticate first
                    if (!TryAutoConnectRemote(config, timeoutMs))
                    {
                        return false;
                    }
                }
                else
                {
                    // Local radio - just start discovery
                    LocalRadios();
                }

                // Wait for the radio to be discovered
                var startTime = DateTime.Now;
                Radio foundRadio = null;

                while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                {
                    foundRadio = findRadioInAPI(config.RadioSerial);
                    if (foundRadio != null)
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(100);
                }

                if (foundRadio == null)
                {
                    Tracing.TraceLine($"TryAutoConnect: radio serial {config.RadioSerial} not found within {timeoutMs}ms timeout. myRadioList has {myRadioList.Count} radios.", TraceLevel.Warning);
                    foreach (Radio r in myRadioList)
                    {
                        Tracing.TraceLine($"  myRadioList entry: serial={r.Serial} name={r.Nickname} status={r.Status}", TraceLevel.Info);
                    }
                    ScreenReaderOutput.Speak($"{config.RadioName} not found", true);
                    return false;
                }

                // Connect to the radio
                Tracing.TraceLine($"TryAutoConnect: found radio, connecting with lowBW={config.LowBandwidth}", TraceLevel.Info);
                bool connected = Connect(config.RadioSerial, config.LowBandwidth);

                if (connected)
                {
                    ScreenReaderOutput.Speak($"Connected to {config.RadioName}", true);
                }
                else
                {
                    ScreenReaderOutput.Speak($"Failed to connect to {config.RadioName}", true);
                }

                return connected;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"TryAutoConnect exception: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Handles remote radio auto-connect: silent authentication using saved account.
        /// </summary>
        private bool TryAutoConnectRemote(AutoConnectConfig config, int timeoutMs)
        {
            // Find the saved SmartLink account
            var account = AccountManager.GetAccountByEmail(config.SmartLinkAccountEmail);

            // Fallback: if email is empty in config (pre-fix configs), use the first saved account
            if (account == null && string.IsNullOrWhiteSpace(config.SmartLinkAccountEmail) && AccountManager.Accounts.Count > 0)
            {
                account = AccountManager.Accounts[0];
                Tracing.TraceLine($"TryAutoConnectRemote: config email was empty, falling back to first saved account: {account.Email}", TraceLevel.Info);
                // Update config in memory so it's correct if saved later
                config.SmartLinkAccountEmail = account.Email;
            }

            if (account == null)
            {
                Tracing.TraceLine($"TryAutoConnectRemote: no saved account for '{config.SmartLinkAccountEmail}'", TraceLevel.Warning);
                ScreenReaderOutput.Speak("SmartLink account not found. Please log in manually.", true);
                return false;
            }

            // Always refresh the token on auto-connect startup.
            // Saved tokens may have been invalidated server-side by other login sessions.
            if (!string.IsNullOrEmpty(account.RefreshToken))
            {
                Tracing.TraceLine("TryAutoConnectRemote: proactively refreshing token for auto-connect", TraceLevel.Info);

                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (AggregateException ex)
                {
                    Tracing.TraceLine($"TryAutoConnectRemote: token refresh exception: {ex.InnerException?.Message ?? ex.Message}", TraceLevel.Error);
                    refreshed = false;
                }

                if (refreshed)
                {
                    Tracing.TraceLine($"TryAutoConnectRemote: token refreshed successfully, new expiry: {account.ExpiresAt}", TraceLevel.Info);
                }
                else
                {
                    Tracing.TraceLine("TryAutoConnectRemote: token refresh failed, will try with existing token", TraceLevel.Warning);
                }
            }

            string jwt = account.IdToken;

            // Store current account for potential re-auth
            _currentAccount = account;

            // Check if the JWT's own exp claim has passed.
            // Auth0's frtest tenant doesn't return a new id_token on refresh,
            // so even after a successful refresh the saved JWT may be expired.
            // In that case, perform a silent interactive login to get a fresh token.
            if (string.IsNullOrEmpty(jwt) || SmartLinkAccountManager.IsJwtExpired(jwt))
            {
                Tracing.TraceLine("TryAutoConnectRemote: saved id_token JWT is expired, performing silent re-login", TraceLevel.Info);

                jwt = PerformNewLogin(title: "Connecting to Radio");
                if (string.IsNullOrEmpty(jwt))
                {
                    Tracing.TraceLine("TryAutoConnectRemote: silent re-login failed or cancelled", TraceLevel.Error);
                    return false;
                }

                // PerformNewLogin already updates _currentAccount and saves tokens
                Tracing.TraceLine("TryAutoConnectRemote: got fresh token from re-login", TraceLevel.Info);
            }

            // Connect to SmartLink server (this will trigger radio discovery)
            apiInit();
            bool connected = ConnectToSmartLink(jwt);

            if (!connected)
            {
                Tracing.TraceLine("TryAutoConnectRemote: SmartLink connection failed", TraceLevel.Error);
                ScreenReaderOutput.Speak("SmartLink connection failed", true);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Start radio activity
        /// </summary>
        public bool Start()
        {
            FilterObj = new Flex6300Filters(this); // Sets up RigFields.

            await(() =>
            {
                return initialFreeSlices != -1;
            }, 5000);
            // Need at least 1 slice.
            if (initialFreeSlices <= 0)
            {
                Tracing.TraceLine("start: couldn't get a slice", TraceLevel.Error);
                raiseNoSliceError(noSlice);
                return false;
            }

            // Must have an antenna.
            if (!await(() =>
            {
                return ((theRadio.RXAntList != null) && (theRadio.RXAntList.Length > 0));
            }, 5000))
            {
                Tracing.TraceLine("start:no RX antenna", TraceLevel.Error);
                raiseNoSliceError(noRXAnt);
                return false;
            }

            // wait until the station name is set.
            if (await(() =>
            {
                GUIClient client = TheGuiClient;
                if (client == null) return false;
                return (client.Station == Callouts.StationName);
            }, 10000))
            {
                Tracing.TraceLine("start:station name set " + Callouts.StationName, TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("start:didn't get a station name:should be " + Callouts.StationName, TraceLevel.Error);
                raiseNoSliceError(noStation);
                return false;
            }
            mainThread = new Thread(mainThreadProc);
            mainThread.Name = "mainThread";
            mainThread.Start();
            Thread.Sleep(0);
            return true;
        }

        internal bool Disconnecting = false;
        /// <summary>
        /// Disconnect from the connected radio.
        /// Also disconnects from the wan if appropriate.
        /// </summary>
        public void Disconnect()
        {
            Tracing.TraceLine("Disconnect:" + (string)((theRadio == null) ? "null" : theRadio.Serial), TraceLevel.Info);
            if (theRadio == null) return;
            Disconnecting = true;

            try
            {
                if ((mainThread != null) && mainThread.IsAlive)
                {
                    // Stop the main thread.
                    Tracing.TraceLine("Disconnect:stopping main thread", TraceLevel.Info);
                    stopMainThread = true;
                    if (mainThread.Join(3000))
                    {
                        Tracing.TraceLine("Disconnect:main thread stopped", TraceLevel.Info);
                    }
                    else
                    {
                        Tracing.TraceLine("Disconnect:main thread didn't stop", TraceLevel.Error);
                        mainThread.Abort();
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("Disconnect:mainThread:" + ex.Message, TraceLevel.Error);
            }
            mainThread = null;

            if (theRadio.Connected)
            {
                theRadio.Disconnect();
                if (!await(() =>
                {
                    return !theRadio.Connected;
                }, 30000))
                {
                    Tracing.TraceLine("Disconnect:the radio didn't disconnect", TraceLevel.Info);
                }

                PCAudio = false;

                if (RemoteRig)
                {
                    wan.Disconnect();
                    wan = null;
                }
                theRadio = null;
            }
        }

        private bool _IsConnected = false; // set in radioPropertyChangedHandler
        /// <summary>
        /// True if connected.
        /// </summary>
        public bool IsConnected
        {
            get { return _IsConnected; }
        }

        /// <summary>
        /// Reboot the radio
        /// </summary>
        /// <param name="disconnect">true to disconnect first</param>
        public void Reboot(bool disconnect = false)
        {
            Tracing.TraceLine("Reboot:" + disconnect.ToString(), TraceLevel.Info);
            if (theRadio != null)
            {
                Radio r = theRadio;
                if (disconnect & IsConnected) Disconnect();
                r.RebootRadio();
            }
        }

        /// <summary>
        /// Clear the web cache.
        /// </summary>
        public void ClearWebCache()
        {
            Tracing.TraceLine("ClearWebCache", TraceLevel.Info);
            WebBrowserHelper.ClearCache();
        }

        // WAN routines.
        #region WAN
        private List<Radio> radios;
        private bool wanListReceived = false;
        private void wanRadioListReceivedHandler(List<Radio> lst)
        {
            try
            {
                Tracing.TraceLine("wanRadioListReceivedHandler:" + lst.Count, TraceLevel.Info);
                radios = lst;
                wanListReceived = true;
                foreach (Radio r in lst)
                {
                    Radio oldRadio = findRadioInAPI(r.Serial);
                    if (oldRadio == null)
                    {
                        // In v4 API the helper is private; directly raise our local handler.
                        radioAddedHandler(r);
                    }
                    else
                    {
                        // only once
                        UpdateRadioDiscoveryFields(r, oldRadio);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("wanRadioListReceivedHandler:exception:" + ex.Message, TraceLevel.Error);
            }
        }
        private void UpdateRadioDiscoveryFields(Radio newRadio, Radio oldRadio)
        {
            Tracing.TraceLine("UpdateRadioDiscoveryFields:" + newRadio.Nickname + ' ' + newRadio.Callsign, TraceLevel.Info);
            if (oldRadio.Nickname != newRadio.Nickname)
                oldRadio.Nickname = newRadio.Nickname;
            if (oldRadio.Callsign != newRadio.Callsign)
                oldRadio.Callsign = newRadio.Callsign;
            if (oldRadio.Status != newRadio.Status)
                oldRadio.Status = newRadio.Status;
            if (oldRadio.GuiClientIPs != newRadio.GuiClientIPs)
                oldRadio.GuiClientIPs = newRadio.GuiClientIPs;
            if (oldRadio.GuiClientHosts != newRadio.GuiClientHosts)
                oldRadio.GuiClientHosts = newRadio.GuiClientHosts;
            if (oldRadio.PublicTlsPort != newRadio.PublicTlsPort)
                oldRadio.PublicTlsPort = newRadio.PublicTlsPort;
            if (oldRadio.PublicUdpPort != newRadio.PublicUdpPort)
                oldRadio.PublicUdpPort = newRadio.PublicUdpPort;
            if (oldRadio.IsPortForwardOn != newRadio.IsPortForwardOn)
                oldRadio.IsPortForwardOn = newRadio.IsPortForwardOn;
            if (oldRadio.Version != newRadio.Version)
                oldRadio.Version = newRadio.Version;
            if (oldRadio.RequiresHolePunch != newRadio.RequiresHolePunch)
                oldRadio.RequiresHolePunch = newRadio.RequiresHolePunch;
            if (oldRadio.NegotiatedHolePunchPort != newRadio.NegotiatedHolePunchPort)
                oldRadio.NegotiatedHolePunchPort = newRadio.NegotiatedHolePunchPort;
            if (oldRadio.MaxLicensedVersion != newRadio.MaxLicensedVersion)
                oldRadio.MaxLicensedVersion = newRadio.MaxLicensedVersion;
            if (oldRadio.RequiresAdditionalLicense != newRadio.RequiresAdditionalLicense)
                oldRadio.RequiresAdditionalLicense = newRadio.RequiresAdditionalLicense;
            if (oldRadio.RadioLicenseId != newRadio.RadioLicenseId)
                oldRadio.RadioLicenseId = newRadio.RadioLicenseId;
            if (oldRadio.LowBandwidthConnect != newRadio.LowBandwidthConnect)
                oldRadio.LowBandwidthConnect = newRadio.LowBandwidthConnect;
            oldRadio.UpdateGuiClientsList(newGuiClients: newRadio.GuiClients);
        }

        private WanServer wan;
        private string wanConnectionHandle;
        private bool WanRadioConnectReadyReceived = false;
        private void WanRadioConnectReadyHandler(string handle, string serial)
        {
            Tracing.TraceLine("WanRadioConnectReadyHandler:" + handle + ' ' + serial);
            wanConnectionHandle = handle;
            WanRadioConnectReadyReceived = true;
        }

        // SmartLink account manager for saved credentials
        private static SmartLinkAccountManager _accountManager;
        private static SmartLinkAccountManager AccountManager
        {
            get
            {
                if (_accountManager == null)
                {
                    _accountManager = new SmartLinkAccountManager();
                    _accountManager.LoadAccounts();
                }
                return _accountManager;
            }
        }

        // Current SmartLink account (for token refresh on re-auth)
        private SmartLinkAccount _currentAccount;

        /// <summary>
        /// Gets the email address of the currently active SmartLink account, if any.
        /// Used by the rig selector to save the account email in auto-connect config.
        /// </summary>
        public string CurrentSmartLinkEmail => _currentAccount?.Email ?? "";

        [Obsolete("Use setupRemote() which handles accounts automatically")]
        private string[] tokens;

        private bool setupRemote()
        {
            bool rv = false;
            string jwt = null;

            // Check for saved accounts
            var accounts = AccountManager.Accounts;

            if (accounts.Count > 0)
            {
                // Show account selector
                using (var selector = new SmartLinkAccountSelector(AccountManager))
                {
                    if (selector.ShowDialog() != DialogResult.OK)
                    {
                        Tracing.TraceLine("setupRemote: user cancelled account selection", TraceLevel.Info);
                        goto setupRemoteDone;
                    }

                    if (selector.NewLoginRequested)
                    {
                        // User wants to log in with a new account - force Auth0 to show login page
                        jwt = PerformNewLogin(forceNewLogin: true);
                    }
                    else if (selector.SelectedAccount != null)
                    {
                        // Use saved account
                        _currentAccount = selector.SelectedAccount;
                        jwt = GetJwtFromSavedAccount(_currentAccount);
                    }
                }
            }
            else
            {
                // No saved accounts - go straight to login
                jwt = PerformNewLogin();
            }

            if (string.IsNullOrEmpty(jwt))
            {
                Tracing.TraceLine("setupRemote: no jwt obtained", TraceLevel.Error);
                goto setupRemoteDone;
            }

            // Connect to SmartLink server
            rv = ConnectToSmartLink(jwt);

            // If connection failed and we have a saved account, try refreshing the token
            // and retrying once. The saved token may have been invalidated server-side.
            if (!rv && _currentAccount != null && !string.IsNullOrEmpty(_currentAccount.RefreshToken))
            {
                Tracing.TraceLine("setupRemote: first connect failed, attempting token refresh and retry", TraceLevel.Info);

                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(_currentAccount)).Result;
                }
                catch (AggregateException)
                {
                    refreshed = false;
                }

                if (refreshed)
                {
                    Tracing.TraceLine("setupRemote: token refreshed, retrying connection", TraceLevel.Info);
                    rv = ConnectToSmartLink(_currentAccount.IdToken);
                }

                if (!rv)
                {
                    // Refresh didn't help - offer fresh login
                    Tracing.TraceLine("setupRemote: retry failed, offering fresh login", TraceLevel.Warning);
                    var result = MessageBox.Show(
                        "Could not connect to SmartLink with the saved account.\n\n" +
                        "Would you like to log in again?",
                        "Connection Failed",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button1);

                    if (result == DialogResult.Yes)
                    {
                        jwt = PerformNewLogin();
                        if (!string.IsNullOrEmpty(jwt))
                        {
                            rv = ConnectToSmartLink(jwt);
                        }
                    }
                }
            }

            setupRemoteDone:
            return rv;
        }

        /// <summary>
        /// Performs a new login via WebView2 and optionally saves the account.
        /// </summary>
        /// <param name="forceNewLogin">When true, forces Auth0 to show the login page
        /// even if a session already exists (used when adding a new account).</param>
        /// <param name="title">Custom title for the auth form window (e.g., "Connecting to Radio"
        /// for auto-connect flows instead of the default "SmartLink Authentication").</param>
        private string PerformNewLogin(bool forceNewLogin = false, string title = null)
        {
            string jwt = null;

            using (var form = (AuthFormWebView2)AuthForm.CreateAuthForm())
            {
                form.ForceNewLogin = forceNewLogin;
                if (!string.IsNullOrEmpty(title))
                {
                    form.Text = title;
                }

                if (form.ShowDialog() != DialogResult.OK)
                {
                    Tracing.TraceLine("setupRemote: auth form cancelled or failed", TraceLevel.Info);
                    return null;
                }

                jwt = form.IdToken;

                if (string.IsNullOrEmpty(jwt))
                {
                    Tracing.TraceLine("setupRemote: no id_token from auth form", TraceLevel.Error);
                    return null;
                }

                // Save or update the account
                if (!string.IsNullOrEmpty(form.RefreshToken))
                {
                    // Check if this email already has a saved account
                    var existingAccount = AccountManager.GetAccountByEmail(form.Email);

                    if (existingAccount != null)
                    {
                        // Silently update the existing account's tokens
                        existingAccount.IdToken = form.IdToken;
                        existingAccount.RefreshToken = form.RefreshToken;
                        existingAccount.ExpiresAt = DateTime.UtcNow.AddSeconds(form.ExpiresIn > 0 ? form.ExpiresIn : 86400);
                        AccountManager.SaveAccount(existingAccount);
                        _currentAccount = existingAccount;

                        Tracing.TraceLine($"setupRemote: updated existing account for {form.Email}", TraceLevel.Info);
                    }
                    else
                    {
                        // New account - ask if they want to save it
                        var saveResult = MessageBox.Show(
                            $"Would you like to save this SmartLink account?\n\n" +
                            $"Email: {form.Email}\n\n" +
                            "You won't need to log in again next time.",
                            "Save Account?",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button1);

                        if (saveResult == DialogResult.Yes)
                        {
                            // Prompt for friendly name
                            string friendlyName = PromptForAccountName(form.Email);

                            var account = new SmartLinkAccount
                            {
                                FriendlyName = friendlyName,
                                Email = form.Email ?? string.Empty,
                                IdToken = form.IdToken,
                                RefreshToken = form.RefreshToken,
                                ExpiresAt = DateTime.UtcNow.AddSeconds(form.ExpiresIn > 0 ? form.ExpiresIn : 86400),
                                LastUsed = DateTime.UtcNow
                            };

                            AccountManager.SaveAccount(account);
                            _currentAccount = account;

                            ScreenReaderOutput.Speak("Account saved", true);
                            Tracing.TraceLine($"setupRemote: saved account for {form.Email}", TraceLevel.Info);
                        }
                    }
                }

                // Legacy compatibility
                #pragma warning disable CS0618
                tokens = new[] { $"id_token={jwt}" };
                #pragma warning restore CS0618
            }

            return jwt;
        }

        /// <summary>
        /// Gets JWT from a saved account, refreshing if necessary.
        /// </summary>
        private string GetJwtFromSavedAccount(SmartLinkAccount account)
        {
            // If we already have an active WAN connection, the previous JWT may have been
            // consumed by the server. Always refresh to get a fresh token for re-registration.
            bool needsRefresh = AccountManager.IsTokenExpired(account);
            if (!needsRefresh && wan != null && wan.IsConnected)
            {
                Tracing.TraceLine("setupRemote: existing WAN connection detected, refreshing token for re-registration", TraceLevel.Info);
                needsRefresh = true;
            }

            // Check if token is expired or needs refresh
            if (needsRefresh)
            {
                Tracing.TraceLine("setupRemote: token expired, attempting refresh", TraceLevel.Info);

                // Try to refresh - use Task.Run to avoid sync context deadlock
                // (RefreshTokenAsync uses await internally, .Wait() on UI thread would deadlock)
                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(account)).Result;
                }
                catch (AggregateException)
                {
                    refreshed = false;
                }

                if (!refreshed)
                {
                    Tracing.TraceLine("setupRemote: token refresh failed, need re-auth", TraceLevel.Warning);

                    MessageBox.Show(
                        "Your saved login has expired and could not be refreshed.\n\n" +
                        "Please log in again.",
                        "Session Expired",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    // Fall back to new login
                    return PerformNewLogin();
                }

            }

            // Mark account as used
            AccountManager.MarkAccountUsed(account);

            return account.IdToken;
        }

        /// <summary>
        /// Prompts user for a friendly name for the account.
        /// </summary>
        private string PromptForAccountName(string defaultEmail)
        {
            using (var inputForm = new Form())
            {
                inputForm.Text = "Name This Account";
                inputForm.Size = new System.Drawing.Size(400, 150);
                inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                inputForm.StartPosition = FormStartPosition.CenterParent;
                inputForm.MaximizeBox = false;
                inputForm.MinimizeBox = false;

                var label = new Label
                {
                    Text = "Enter a friendly name for this account (e.g., \"Home Shack\"):",
                    Location = new System.Drawing.Point(12, 12),
                    AutoSize = true
                };

                var textBox = new TextBox
                {
                    Text = defaultEmail ?? "My SmartLink Account",
                    Location = new System.Drawing.Point(12, 35),
                    Size = new System.Drawing.Size(360, 23),
                    AccessibleName = "Account name"
                };
                textBox.SelectAll();

                var okButton = new Button
                {
                    Text = "OK",
                    Location = new System.Drawing.Point(216, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.OK
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(297, 70),
                    Size = new System.Drawing.Size(75, 28),
                    DialogResult = DialogResult.Cancel
                };

                inputForm.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });
                inputForm.AcceptButton = okButton;
                inputForm.CancelButton = cancelButton;

                if (inputForm.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text))
                {
                    return textBox.Text.Trim();
                }
            }

            return defaultEmail ?? "SmartLink Account";
        }

        /// <summary>
        /// Connects to SmartLink server with the given JWT.
        /// </summary>
        private bool ConnectToSmartLink(string jwt)
        {
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
                wan.WanApplicationRegistrationInvalid += WanApplicationRegistrationInvalidHandler;

                wan.Connect();
                if (!wan.IsConnected)
                {
                    Tracing.TraceLine("setupRemote: not connected!", TraceLevel.Error);
                    return false;
                }

                Tracing.TraceLine("setupRemote: SendRegisterApplicationMessageToServer: " + API.ProgramName + ' ' + "Win10" + ' ' + jwt.Substring(0, Math.Min(20, jwt.Length)) + "...", TraceLevel.Info);
                WanServer.WanRadioRadioListRecieved += new WanServer.WanRadioRadioListRecievedEventHandler(wanRadioListReceivedHandler);
                wanListReceived = false;
                wan.SendRegisterApplicationMessageToServer(API.ProgramName, "Win10", jwt);
                if (!await(() => { return wanListReceived; }, 10000))
                {
                    Tracing.TraceLine("ConnectToSmartLink: timed out waiting for radio list (10s)", TraceLevel.Error);
                    return false;
                }

                Tracing.TraceLine($"ConnectToSmartLink: received {radios.Count} radio(s), myRadioList has {myRadioList.Count} entries", TraceLevel.Info);
                foreach (var r in radios)
                {
                    Tracing.TraceLine($"  WAN radio: serial={r.Serial} name={r.Nickname} status={r.Status}", TraceLevel.Info);
                }

                if (radios.Count == 0)
                {
                    Tracing.TraceLine("ConnectToSmartLink: no radios in list", TraceLevel.Error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("setupRemote: exception in ConnectToSmartLink: " + ex.Message, TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Handles invalid token registration from SmartLink server.
        /// </summary>
        private void WanApplicationRegistrationInvalidHandler()
        {
            Tracing.TraceLine("WanApplicationRegistrationInvalid: token rejected by server", TraceLevel.Warning);

            // If we have a current account, try to refresh and reconnect
            if (_currentAccount != null && !string.IsNullOrEmpty(_currentAccount.RefreshToken))
            {
                Tracing.TraceLine("Attempting token refresh after registration invalid", TraceLevel.Info);

                bool refreshed = false;
                try
                {
                    refreshed = Task.Run(() => AccountManager.RefreshTokenAsync(_currentAccount)).Result;
                }
                catch (AggregateException)
                {
                    refreshed = false;
                }

                if (refreshed && !SmartLinkAccountManager.IsJwtExpired(_currentAccount.IdToken))
                {
                    // Retry connection with new token (only if the JWT itself is still valid)
                    Tracing.TraceLine("Token refreshed, retrying SmartLink registration", TraceLevel.Info);

                    // Re-register with new token
                    wan.SendRegisterApplicationMessageToServer(API.ProgramName, "Win10", _currentAccount.IdToken);
                    return;
                }

                if (refreshed)
                {
                    Tracing.TraceLine("WanApplicationRegistrationInvalid: refresh succeeded but JWT exp is passed, need re-login", TraceLevel.Warning);
                }
            }

            // Refresh failed or no account - offer to re-authenticate
            // Must marshal to UI thread since this handler is called from a background thread
            Tracing.TraceLine("WanApplicationRegistrationInvalid: refresh failed, prompting re-login", TraceLevel.Warning);

            var mainForm = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            if (mainForm != null && mainForm.InvokeRequired)
            {
                mainForm.BeginInvoke((Action)PromptReLogin);
            }
            else
            {
                PromptReLogin();
            }
        }

        /// <summary>
        /// Prompts user to re-login after session invalidation. Must run on UI thread.
        /// </summary>
        private void PromptReLogin()
        {
            var result = MessageBox.Show(
                "Your SmartLink session is no longer valid.\n\n" +
                "Would you like to log in again?",
                "Session Invalid",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Yes)
            {
                string jwt = PerformNewLogin();
                if (!string.IsNullOrEmpty(jwt) && wan != null && wan.IsConnected)
                {
                    wan.SendRegisterApplicationMessageToServer(API.ProgramName, "Win10", jwt);
                }
            }
        }

        private bool sendRemoteConnect(Radio r)
        {
            Tracing.TraceLine("sendRemoteConnect: " + r.Serial, TraceLevel.Info);
            WanRadioConnectReadyReceived = false;
            // WanRadioConnectReadyHandler already added.
            wan.SendConnectMessageToRadio(r.Serial, 0);
            if (!await(() => { return WanRadioConnectReadyReceived; }, 5000))
            {
                Tracing.TraceLine("sendRemoteConnect:Radio not ready for connect.", TraceLevel.Error);
                return false;
            }
            r.WANConnectionHandle = wanConnectionHandle;
            return true;
        }
        #endregion

        // tools
        #region tools
        internal delegate bool awaitExp();
        /// <summary>
        /// Await the specified condition.
        /// </summary>
        /// <param name="exp">function that returns the condition</param>
        /// <param name="ms">milliseconds to wait.</param>
        /// <param name="interval">optional interval to check</param>
        /// <returns>true if condition met.</returns>
        internal static bool await(awaitExp exp, int ms, int interval)
        {
            int sanity = ms / interval;
            bool rv = false;
            while (sanity-- > 0)
            {
                rv = exp();
                if (rv) break;
                Thread.Sleep(interval);
            }
            return rv;
        }
        internal static bool await(awaitExp exp, int ms)
        {
            return await(exp, ms, 25);
        }

        internal delegate bool AssertDel();
        /// <summary>
        /// assert this condition, exception if fails.
        /// </summary>
        /// <param name="exp">condition</param>
        /// <param name="msg">exception text</param>
        internal static void Assert(AssertDel exp, string msg)
        {
            if (!exp())
            {
                throw new Exception(msg);
            }
        }

        internal static void DbgTrace(string text)
        {
#if DBGTrace
            Tracing.TraceLine(text);
#endif
        }
        #endregion

        // Implement Dispose().
        #region dispose
        private bool disposed = false;
        private Component component = new Component();
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Tracing.TraceLine("FlexBase.Dispose:" + disposing.ToString(), TraceLevel.Info);
            if (!disposed)
            {
                if (disposing)
                {
                    component.Dispose();
                }

                if (theRadio != null)
                {
                    saveNewGlobalProfile(); // if any

                    Disconnect();
                }

                if (wan != null)
                {
                    wan.Disconnect();
                    wan = null;
                }

                if (_apiInit)
                {
                    _apiInit = false;
                    API.CloseSession();
                }

                if (RigFields != null)
                {
                    // The caller should have removed the user control from their form.
                    ((Flex6300Filters)RigFields.RigControl).Close(); // Remove int handlers
                    RigFields.Close();
                    RigFields = null;
                }


                disposed = true;
            }
        }

        ~FlexBase()
        {
            Dispose(false);
        }
        #endregion

        /// <summary>
        /// Off/On values for use by the rigs
        /// </summary>
        public enum OffOnValues
        {
            off,
            on
        }

        /// <summary>
        /// return the toggle of the OffOnValue
        /// </summary>
        /// <param name="value"></param>
        /// <returns>Toggled OffOnValue</returns>
        public OffOnValues ToggleOffOn(OffOnValues value)
        {
            return (value == OffOnValues.on) ? OffOnValues.off : OffOnValues.on;
        }

        public class ConnectedArg
        {
            public string Serial;
            public bool Connected;
            internal ConnectedArg(string serial, bool connected)
            {
                Serial = serial;
                Connected = connected;
            }
        }

        public delegate void ConnectedDel(object sender, ConnectedArg arg);
        public event ConnectedDel ConnectedEvent;
        private void raiseConnectedEvent(bool connected)
        {
            if (ConnectedEvent != null)
            {
                Tracing.TraceLine("raiseConnectedEvent:" + connected.ToString(), TraceLevel.Info);
                ConnectedEvent(this, new ConnectedArg(theRadio.Serial, connected));
            }
            else
            {
                Tracing.TraceLine("raiseConnectedEvent:not handled:" + connected.ToString(), TraceLevel.Info);
            }
        }

        internal delegate void UpdateConfiguredTNFsDel(TNF tnf);
        internal UpdateConfiguredTNFsDel UpdateConfiguredTNFs = null;

#if KeepAlive
        class keepAlive_t
        {
            private System.Threading.Timer theTimer;
            private FlexBase parent;
            private const int keepAlivePeriod = 1000 * 5; // 5 seconds
            public keepAlive_t(FlexBase p)
            {
                parent = p;
                theTimer = new System.Threading.Timer(timerCallback, null, keepAlivePeriod, keepAlivePeriod);
            }
            private void timerCallback(object state)
            {
                if (parent.Disconnecting) return;

                try
                {
                    if (!parent.IsConnected)
                    {
                        Tracing.TraceLine("keepAlive power off", TraceLevel.Info);
                        theTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        parent.raisePowerEvent(false);
                    }
                }
                catch(Exception ex)
                {
                    Tracing.TraceLine("keepAlive exception:" + ex.Message, TraceLevel.Error);
                }
            }
            public void Done()
            {
                Tracing.TraceLine("keepAlive_t.Done", TraceLevel.Info);
                theTimer.Dispose();
            }
        }
#endif

        private bool maintainAudio;
        private ATUTuneStatus originalATUStatus = ATUTuneStatus.None;
        private bool oldATUEnable = false; // false is the default, see Flex6300.
        private bool globalProfileLoaded; // see GetProfileInfo().
        private string globalProfileDesired; // see GetProfileInfo().
        internal bool ExportComplete;
        internal string ExportException;
        private void HookFeatureLicense(Radio radio)
        {
            if (trackedFeatureLicense != null)
            {
                trackedFeatureLicense.PropertyChanged -= FeatureLicense_PropertyChanged;
                trackedFeatureLicense = null;
            }

            var license = radio?.FeatureLicense;
            if (license != null)
            {
                trackedFeatureLicense = license;
                trackedFeatureLicense.PropertyChanged += FeatureLicense_PropertyChanged;
            }

            RaiseFeatureLicenseChanged();
        }

        private void FeatureLicense_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            RaiseFeatureLicenseChanged();
        }

        private void RaiseFeatureLicenseChanged()
        {
            FeatureLicenseChanged?.Invoke(this, EventArgs.Empty);
        }
        private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Tracing.TraceLine("propertyChanged:Radio:" + e.PropertyName, TraceLevel.Verbose);
            Radio r = (Radio)sender;
            if (!(r.ClientHandle != 0) & myClient(r.ClientHandle))
            {
                Tracing.TraceLine("propertyChanged:Radio:NotMine:" + e.PropertyName);
            }
            switch (e.PropertyName)
            {
                case "FeatureLicense":
                    HookFeatureLicense(r);
                    break;
                case "ActiveSlice":
                    {
                        Slice s = r.ActiveSlice;
                        if ((s != null) && myClient(s.ClientHandle))
                        {
                            Tracing.TraceLine("ActiveSlice:mine " + SliceToVFO(s), TraceLevel.Info);
                            _RXFrequency = LibFreqtoLong(s.Freq);
                            _RXMode = s.DemodMode;
                            _RXVFO = SliceToVFO(s);
                            s.Panadapter.GetRFGainInfo();
                            FilterObj.RXFreqChange(s);
#if CWMonitor
                            if (useCWMon && (s == VFOToSlice(TXVFO)) && (s.DemodMode == "CW"))
                            {
                                CWMonStart(); // ok if already started.
                            }
#endif
                        }
                        else
                        {
                            Tracing.TraceLine("ActiveSlice:none", TraceLevel.Info);
                        }
                    }
                    break;
                case "ATUEnabled":
                    {
                        Tracing.TraceLine("ATUEnabled:" + theRadio.ATUEnabled.ToString(), TraceLevel.Info);
                        if (oldATUEnable == r.ATUEnabled) return;
                        oldATUEnable = r.ATUEnabled;
                        bool wasEnabled = MyCaps.HasCap(RigCaps.Caps.ATGet);
#if !NoATU
                        if (r.ATUEnabled)
                        {
                            // indicate ATU capable.
                            MyCaps.getCaps = MyCaps.SetCap(MyCaps.getCaps, RigCaps.Caps.ATGet);
                            MyCaps.setCaps = MyCaps.SetCap(MyCaps.setCaps, RigCaps.Caps.ATSet);
                            MyCaps.getCaps = MyCaps.SetCap(MyCaps.getCaps, RigCaps.Caps.ATMems);
                            // Turn off the tuner if was bypassed.
                            // Note the bypass status might happen later.
                            if ((originalATUStatus == ATUTuneStatus.Bypass) |
                                (originalATUStatus == ATUTuneStatus.ManualBypass))
                            {
                                setFlexTunerTypeNotAuto();
                            }
                            else _FlexTunerType = FlexTunerTypes.auto;
                        }
                        else
#endif
                        {
                            // not atu capable, unless the above condition true.
                            MyCaps.getCaps = MyCaps.ResetCap(MyCaps.getCaps, RigCaps.Caps.ATGet);
                            MyCaps.setCaps = MyCaps.ResetCap(MyCaps.setCaps, RigCaps.Caps.ATSet);
                            MyCaps.getCaps = MyCaps.ResetCap(MyCaps.getCaps, RigCaps.Caps.ATMems);
                            setFlexTunerTypeNotAuto();
                        }
                        if (wasEnabled != MyCaps.HasCap(RigCaps.Caps.ATGet))
                        {
                            // enabled status changed.
                            raiseCapsChange(new CapsChangeArg(MyCaps));
                        }
                    }
                    break;
#if !NoATU
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
#endif
                case "Connected":
                    {
                        Tracing.TraceLine("Connected:" + r.Connected.ToString(), TraceLevel.Error);
                        _IsConnected = r.Connected;
#if zero
                        bool justReconnected = false;
                        if (!r.Connected &&
                            !Disconnecting &&
                            !string.IsNullOrEmpty(clientID))
                        {
                            justReconnected = true;
                            theRadio.Connect(clientID);
                        }
                        else
                        {
                            raiseConnectedEvent(r.Connected);
                        }
#endif
                        //raiseConnectedEvent(r.Connected);
                    }
                    break;
                case "CWBreakIn":
                    Tracing.TraceLine("CWBreakIn:" + r.CWBreakIn.ToString(), TraceLevel.Info);
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
                    Tracing.TraceLine("CWSwapPaddles:" + r.CWSwapPaddles, TraceLevel.Info);
                    _CWReverse = r.CWSwapPaddles;
                    break;
                case "DatabaseExportComplete":
                    {
                        Tracing.TraceLine("DatabaseExportComplete:" + r.DatabaseExportComplete.ToString(), TraceLevel.Info);
                        ExportComplete = r.DatabaseExportComplete;
                    }
                    break;
                case "DatabaseExportccccccccccccccccccccccccccc Exception":
                    {
                        Tracing.TraceLine("DatabaseExportException:" + r.DatabaseExportException, TraceLevel.Info);
                        ExportException = r.DatabaseExportException;
                    }
                    break;
                case "DatabaseImportComplete":
                    {
                        Tracing.TraceLine("DatabaseImportComplete:mine " + r.DatabaseImportComplete.ToString(), TraceLevel.Info);
                        if (r.DatabaseImportComplete & !string.IsNullOrEmpty(importDir))
                        {
                            q.Enqueue((FunctionDel)(() => { GetProfileInfo(true); }), "GetProfileInfo");
                        }
                    }
                    break;
                case "DAXOn":
                    {
                        Tracing.TraceLine("DAXOn:" + r.DAXOn.ToString(), TraceLevel.Info);
                        _DAXOnOff = (r.DAXOn) ? OffOnValues.on : OffOnValues.off;
                    }
                    break;
                case "InterlockState":
                    {
                        Tracing.TraceLine("InterlockState:" + r.InterlockState.ToString(), TraceLevel.Info);
                    }
                    break;
                case "IsMuteLocalAudioWhenRemoteOn":
                    {
                        Tracing.TraceLine("IsMuteLocalAudioWhenRemoteOn:" + r.IsMuteLocalAudioWhenRemoteOn.ToString(), TraceLevel.Info);
                    }
                    break;
                case "LineoutMute":
                    {
                        Tracing.TraceLine("LineoutMute:" + r.LineoutMute.ToString(), TraceLevel.Info);
                        if (maintainAudio != r.LineoutMute)
                        {
                            //Tracing.TraceLine("LineoutMute:forced to " + maintainAudio.ToString(), TraceLevel.Info);
                            //LocalAudioMute(maintainAudio);
                        }
                    }
                    break;
                case "Mox":
                    {
                        Tracing.TraceLine("Mox:" + r.Mox.ToString(), TraceLevel.Info);
                        bool oldTransmit = _Transmit;
                        _Transmit = r.Mox;
                        if (_Transmit != oldTransmit)
                        {
                            raiseTransmitChange(_Transmit);
                        }
                    }
                    break;
                case "PanadaptersRemaining":
                    {
                        Tracing.TraceLine("PanadaptersRemaining:" + r.PanadaptersRemaining, TraceLevel.Info);
                        // First one will be the total slices.
                        if (initialFreeSlices == -1)
                        {
                            initialFreeSlices = r.PanadaptersRemaining;
                        }
                    }
                    break;
                case "PersistenceLoaded":
                    Tracing.TraceLine("PersistenceLoaded:" + r.PersistenceLoaded.ToString(), TraceLevel.Info);
                    break;
                case "ProfileGlobalList":
                    {
                        // See if desired global profile is loaded.
                        globalProfileLoaded = r.ProfileGlobalList.Contains(globalProfileDesired);
                        string line = "";
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
                    }
                    break;
                case "ProfileTXList":
                    {
                        string line = "";
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
                case "PTTSource":
                    Tracing.TraceLine("PTTSource:" + r.PTTSource.ToString(), TraceLevel.Info);
                    break;
                case "RFPower":
                    Tracing.TraceLine("RFPower:" + theRadio.RFPower, TraceLevel.Info);
                    _XmitPower = theRadio.RFPower;
                    break;
                case "SimpleVOXEnable":
                    Tracing.TraceLine("SimpleVOXEnable:" + r.SimpleVOXEnable.ToString(), TraceLevel.Info);
                    break;
                case "Status":
                    string status = theRadio.Status;
                    Tracing.TraceLine("radio status:" + status, TraceLevel.Info);
                    break;
                case "TransmitSlice":
                    {
                        Slice s = r.TransmitSlice;
                        if (s == null)
                        {
                            Tracing.TraceLine("TransmitSlice:null", TraceLevel.Info);
                            return;
                        }
                        if (myClient(s.ClientHandle))
                        {
                            Tracing.TraceLine("TransmitSlice:mine " + SliceToVFO(s), TraceLevel.Info);
                        }
                        else
                        {
                            Tracing.TraceLine("TransmitSlice:not mine", TraceLevel.Info);
                        }
                    }
                    break;
                case "TunePower":
                    Tracing.TraceLine("TunePower:" + r.TunePower, TraceLevel.Info);
                    _TunePower = r.TunePower;
                    break;
                case "TX1Enabled":
                    Tracing.TraceLine("TX1Enabled:" + r.TX1Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TX2Enabled":
                    Tracing.TraceLine("TX2Enabled:" + r.TX2Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TX3Enabled":
                    Tracing.TraceLine("TX3Enabled:" + r.TX3Enabled.ToString(), TraceLevel.Info);
                    break;
                case "TXCWMonitorGain":
                    {
                        Tracing.TraceLine("TXCWMonitorGain:" + theRadio.TXCWMonitorGain, TraceLevel.Info);
#if CWMonitor
                        if (useCWMon) CWMon.Volume = theRadio.TXCWMonitorGain;
#endif
                    }
                    break;
                case "TXTune":
                    {
                        Tracing.TraceLine("TXTune:" + r.TXTune.ToString(), TraceLevel.Info);
                        if (r.TXTune)
                        {
                            // Report status if starting up.
                            ATUTuneStatus stat = ATUTuneStatus.InProgress;
                            RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                (FlexTunerType, stat, _SWR));
                        }
                    }
                    break;
            }
        }

        private void slicePropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Slice s = (Slice)sender;
            if (myClient(s.ClientHandle))
            {
                Tracing.TraceLine("propertyChanged:Slice:mine " + e.PropertyName, TraceLevel.Verbose);
                switch (e.PropertyName)
                {
                    case "Active":
                        {
                            Tracing.TraceLine("Active:slice " + s.Index.ToString() + " " + s.Active.ToString(), TraceLevel.Info);
                        }
                        break;
                    case "DemodMode":
                        {
                            Tracing.TraceLine("DemodMode:slice " + s.Index.ToString() + " " + s.DemodMode.ToString(), TraceLevel.Info);
                            if (s.Active) _RXMode = s.DemodMode;
                            if (s.IsTransmitSlice) _TXMode = s.DemodMode;
                            if (s.Active)
                            {
                                FilterObj.RXFreqChange(s);
                            }
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
                                FilterObj.RXFreqChange(s);
                            }
                            if (s.IsTransmitSlice) _TXFrequency = LibFreqtoLong(s.Freq);
                        }
                        break;
                    case "IsTransmitSlice":
                        {
                            Tracing.TraceLine("IsTransmit:slice " + s.Index.ToString() + " " + s.IsTransmitSlice.ToString(), TraceLevel.Info);
                            int vfo = SliceToVFO(s);
                            if (s.IsTransmitSlice)
                            {
                                if (CanTransmit)
                                {
                                    _TXVFO = vfo;
                                    _TXFrequency = LibFreqtoLong(s.Freq);
                                    _TXMode = s.DemodMode;
                                }
                            }
                        }
                        break;
                    case "Mute":
                        {
                            Tracing.TraceLine("slicePropertyChangedHandler:Mute slice:" + s.Index + ' ' + s.Mute.ToString(), TraceLevel.Info);
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
            else
            {
                if (s.ClientHandle != 0) Tracing.TraceLine("propertyChanged:Slice:not mine " + e.PropertyName, TraceLevel.Info);
            }
        }

        private void panadapterPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Panadapter p = (Panadapter)sender;
            if (!(p.ClientHandle != 0) & myClient(p.ClientHandle))
            {
                Tracing.TraceLine("panadapterPropertyChangedHandler:not mine:" + e.PropertyName);
            }
            if (myClient(p.ClientHandle))
            {
                Tracing.TraceLine("panPropertyChanged:mine " + e.PropertyName, TraceLevel.Verbose);
                switch (e.PropertyName)
                {
                    case "Bandwidth":
                        Tracing.TraceLine("Bandwidth:" + p.Bandwidth.ToString(), TraceLevel.Verbose);
                        break;
                    case "CenterFreq":
                        Tracing.TraceLine("CenterFreq:" + p.CenterFreq.ToString(), TraceLevel.Info);
                        break;
                    case "FPS":
                        Tracing.TraceLine("FPS:" + p.FPS.ToString(), TraceLevel.Info);
                        break;
                    case "HighDbm":
                        Tracing.TraceLine("HighDBM:" + p.HighDbm.ToString(), TraceLevel.Verbose);
                        break;
                    case "LowDbm":
                        Tracing.TraceLine("LowDbm:" + p.LowDbm.ToString(), TraceLevel.Verbose);
                        break;
                    //case "Preamp":
                    //    Tracing.TraceLine("Preamp:" + p.Preamp, TraceLevel.Info);
                    //    break;
                    case "RFGain":
                        Tracing.TraceLine("panadapter RFGain:" + p.RFGain.ToString(), TraceLevel.Verbose);
                        //if (p == activePan) _PreAmp = (p.RFGain == PreAmpMax) ? OffOnValues.on : OffOnValues.off;
                        break;
                    case "RFGainLow":
                        Tracing.TraceLine("RFGainLow:" + p.RFGainLow, TraceLevel.Info);
                        RFGainMin = p.RFGainLow;
                        break;
                    case "RFGainHigh":
                        Tracing.TraceLine("RFGainHigh:" + p.RFGainHigh, TraceLevel.Info);
                        RFGainMax = p.RFGainHigh;
                        break;
                    case "RFGainStep":
                        Tracing.TraceLine("RFGainStep:" + p.RFGainStep, TraceLevel.Info);
                        RFGainIncrement = p.RFGainStep;
                        break;
                    case "RFGainMarkers":
                        {
                            string str = "";
                            foreach (int i in p.RFGainMarkers)
                            {
                                str += i.ToString() + ' ';
                            }
                            Tracing.TraceLine("RFGainMarkers:" + str, TraceLevel.Info);
                        }
                        break;
                }
            }
        }

        private void waterfallPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            Waterfall w = (Waterfall)sender;
            if (!(w.ClientHandle != 0) & myClient(w.ClientHandle))
            {
                Tracing.TraceLine("waterfallPropertyChangedHandler:not mine:" + e.PropertyName);
            }
            if (myClient(w.ClientHandle))
            {
                Tracing.TraceLine("waterfallPropertyChanged:mine " + e.PropertyName, TraceLevel.Verbose);
                switch (e.PropertyName)
                {
                    case "FallLineDurationMs":
                        Tracing.TraceLine("FallLineDurationMs:" + w.FallLineDurationMs.ToString(), TraceLevel.Info);
                        break;
                }
            }
        }

        private void tnfPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            // See FlexTNF.cs.
            TNF tnf = (TNF)sender;
            Tracing.TraceLine("propertyChanged:TNF:" + e.PropertyName, TraceLevel.Verbose);
            if (UpdateConfiguredTNFs != null) UpdateConfiguredTNFs(tnf);
        }

        private void messageReceivedHandler(MessageSeverity severity, string message)
        {
            Tracing.TraceLine("message severity:" + severity.ToString() + " " + message, TraceLevel.Error);
        }

        private string clientID;
        private const uint noClient = 0xffffffff;
        private uint clientHandle = noClient;
        private void guiClientAdded(GUIClient client)
        {
            if (client == null) return;

            if (client.IsThisClient)
            {
                clientID = client.ClientID;
                clientHandle = client.ClientHandle;
                lock (theRadio.GuiClientsLockObj)
                {
                    OnlyStation = (theRadio.GuiClients.Count == 1);
                }
                //CanTransmit = PrimaryStation;
                CanTransmit = true;

                if (string.IsNullOrEmpty(client.Station))
                {
                    // Ensure no duplicate name.
                    if (!OnlyStation)
                    {
                        foreach (GUIClient c in theRadio.GuiClients)
                        {
                            if (!myClient(c.ClientHandle) &
                                (c.Station == Callouts.StationName))
                            {
                                Callouts.StationName += '1';
                                Tracing.TraceLine("guiClientAdded:station now will be " + Callouts.StationName, TraceLevel.Error);
                            }
                        }
                    }
                    theRadio.SetClientStationName(Callouts.StationName);
                }

                client.PropertyChanged += new PropertyChangedEventHandler(guiClientPropertyChanged);
                _LocalPTT = client.IsLocalPtt;
            }

            Tracing.TraceLine("guiClientAdded:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);
        }

        private bool myClient(uint handle)
        {
            return ((clientHandle == handle)) ? true : false;
        }

        internal GUIClient TheGuiClient
        {
            get
            {
                GUIClient rv = theRadio.FindGUIClientByClientHandle(clientHandle);
                return rv;
            }
        }

        private void guiClientUpdated(GUIClient client)
        {
            if (client == null) return;

            Tracing.TraceLine("guiClientUpdated:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);
        }

        private void guiClientRemoved(GUIClient client)
        {
            if (client == null) return;

            if (myClient(client.ClientHandle))
            {
                Tracing.TraceLine("guiClientRemoved:my client", TraceLevel.Info);
            }

            Tracing.TraceLine("guiClientRemoved:" +
                "id:" + client.ClientID +
                " my client:" + client.IsThisClient.ToString() +
                " handle:" + client.ClientHandle +
                " program:" + client.Program +
                " station:" + client.Station +
                " is local PTT:" + client.IsLocalPtt.ToString() +
                " is available:" + client.IsAvailable.ToString() +
                " Only:" + OnlyStation.ToString() +
                " CanTransmit:" + CanTransmit.ToString(), TraceLevel.Info);
        }

        // These properties are for my client.
        private void guiClientPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Tracing.TraceLine("guiClientPropertyChanged:" + e.PropertyName, TraceLevel.Verbose);
            GUIClient client = TheGuiClient; // get my client
            switch (e.PropertyName)
            {
                case "IsLocalPtt":
                    {
                        Tracing.TraceLine("guiClientPropertyChanged:IsLocalPTT " + client.IsLocalPtt.ToString(), TraceLevel.Info);
                        _LocalPTT = client.IsLocalPtt;
                    }
                    break;
                case "Station":
                    Tracing.TraceLine("guiClientPropertyChanged:station " + client.Station, TraceLevel.Info);
                    break;
            }
        }

        private bool mySliceAdded;
        private void sliceAdded(Slice slc)
        {
            if (myClient(slc.ClientHandle))
            {
                mySliceAdded = true;
                slc.PropertyChanged += new PropertyChangedEventHandler(slicePropertyChangedHandler);
                slc.MeterAdded += new Slice.MeterAddedEventHandler(meterAdded);
                sMeter_t sMeter = new sMeter_t(this, slc);
                slc.SMeterDataReady += sMeter.sMeterData;
                int ct;
                lock (mySlices)
                {
                    mySlices.Add(slc);
                    ct = mySlices.Count;
                }
                Tracing.TraceLine("sliceAdded:mine " + ct.ToString() + ':' + slc.ToString(), TraceLevel.Info);
                if (slc.IsTransmitSlice)
                {
                    Tracing.TraceLine("sliceAdded:IsTransmitSlice", TraceLevel.Info);
                    _TXVFO = SliceToVFO(slc);
                }
                if (slc.Active)
                {
                    Tracing.TraceLine("sliceAdded:activeSlice", TraceLevel.Info);
                    _RXVFO = SliceToVFO(slc);
                }
            }
            else Tracing.TraceLine("sliceAdded:not mine " + slc.ToString(), TraceLevel.Info);
        }

        private void sliceRemoved(Slice slc)
        {
            if (myClient(slc.ClientHandle))
            {
                int ct;
                if (mySlices != null)
                {
                    lock (mySlices)
                    {
                        mySlices.Remove(slc);
                        ct = mySlices.Count;
                    }
                    Tracing.TraceLine("sliceRemoved:mine, new count:" + ct.ToString() + ':' + slc.ToString(), TraceLevel.Info);
                    // Note: The user can't remove the active or transmit slices.
                }
            }
            else Tracing.TraceLine("sliceRemoved:not mine" + slc.ToString(), TraceLevel.Info);
        }

        internal Panadapter Panadapter
        {
            get
            {
                Panadapter rv = null;
                if (theRadio.ActiveSlice != null) rv = theRadio.ActiveSlice.Panadapter;
                return rv;
            }
        }
        internal List<Waterfall> waterfallList;
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
                lock (waterfallList)
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
            }
            return rv;
        }
        private List<Panadapter> myPanAdapters = new List<Panadapter>();
        private void panadapterAdded(Panadapter pan, Waterfall fall)
        {
            if (myClient(pan.ClientHandle))
            {
                if (waterfallList == null) waterfallList = new List<Waterfall>();
                lock (waterfallList)
                {
                    waterfallList.Add(fall);
                }
                pan.Width = 5000;
                pan.PropertyChanged += new PropertyChangedEventHandler(panadapterPropertyChangedHandler);
                int ct;
                lock (myPanAdapters)
                {
                    myPanAdapters.Add(pan);
                    ct = myPanAdapters.Count;
                }
                Tracing.TraceLine("panadapterAdded:mine " + ct.ToString() + ':' + pan.ToString(), TraceLevel.Info);
            }
            else Tracing.TraceLine("panadapterAdded:not mine " + pan.ToString(), TraceLevel.Info);
        }

        internal int MyNumPanadapters
        {
            get
            {
                lock (myPanAdapters)
                {
                    return myPanAdapters.Count;
                }
            }
        }

        private void panAdapterRemoved(Panadapter pan)
        {
            if (myClient(pan.ClientHandle))
            {
                int ct;
                lock (myPanAdapters)
                {
                    myPanAdapters.Remove(pan);
                    ct = myPanAdapters.Count;
                }
                Tracing.TraceLine("panadapterRemoved:new count:" + ct.ToString() + ':' + pan.ToString(), TraceLevel.Info);
            }
            else Tracing.TraceLine("panadapterRemoved:not mine", TraceLevel.Info);
        }
        private void waterfallRemoved(Waterfall fall)
        {
            Tracing.TraceLine("waterfallRemoved", TraceLevel.Info);
            if (waterfallList != null)
            {
                lock (waterfallList)
                {
                    if (waterfallList.Contains(fall))
                    {
                        waterfallList.Remove(fall);
                    }
                }
            }
        }

        internal List<TNF> TNFs
        {
            get { return theRadio.TNFList; }
        }
        private void tnfAdded(TNF tnf)
        {
            Tracing.TraceLine("tnfAdded:" + tnf.ToString(), TraceLevel.Info);
            tnf.PropertyChanged += new PropertyChangedEventHandler(tnfPropertyChangedHandler);
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
            return _SWR.ToString("f1");
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

        private void txBandSettingsHandler(TxBandSettings settings)
        {
            Tracing.TraceLine("txBandSettingsHandler:" + settings.BandName, TraceLevel.Info);
        }

        private float _PATempData;
        private void PATempDataHandler(float data)
        {
            Tracing.TraceLine("PATempDataHandler:" + data.ToString(), TraceLevel.Verbose);
            _PATempData = data;
        }

        /// <summary>
        /// PA temperature in DGC.
        /// </summary>
        internal float PATemp { get { return _PATempData; } }

        private float _VoltsData;
        private void VoltsDataHandler(float data)
        {
            Tracing.TraceLine("VoltsDataHandler:" + data.ToString(), TraceLevel.Verbose);
            _VoltsData = data;
        }

        /// <summary>
        /// Voltage
        /// </summary>
        internal float Volts { get { return _VoltsData; } }

        private void meterAdded(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterAdded:slice " + slc.Index.ToString() + ' ' + m.ToString(), TraceLevel.Info);
        }

        private void meterRemoved(Slice slc, Meter m)
        {
            Tracing.TraceLine("meterRemoved:" + m.ToString(), TraceLevel.Info);
        }

        private class sMeter_t
        {
            private Slice s;
            private FlexBase parent;
            public void sMeterData(float data)
            {
                // Only report for the active slice.
                if (s.Active)
                {
                    Tracing.TraceLine("sMeterData:" + s.Index + ' ' + data.ToString(), TraceLevel.Verbose);
                    parent._SMeter = (int)data;
                }
            }

            internal sMeter_t(FlexBase p, Slice slc)
            {
                parent = p;
                s = slc;
            }
        }

        private bool _Transmit;
        /// <summary>
        /// True if transmitting
        /// </summary>
        public bool Transmit
        {
            get
            {
                return _Transmit;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.Mox = value; }), "Mox");
            }
        }

        /// <summary>
        /// True if rig is on the WAN.
        /// </summary>
        public bool RemoteRig
        {
            get { return theRadio.IsWan; }
        }

        private int firstCharID = -1;
        private StringBuilder sentChars = new StringBuilder();
        private void charSentHandler(int id)
        {
            Tracing.TraceLine("charSent:" + id, TraceLevel.Info);
            if (firstCharID == -1) firstCharID = id;
        }

        public bool SmeterInDBM = false;
        /// <summary>
        /// Calibrated S-Meter/power
        /// </summary>
        /// <remarks>Smeter and forward power are in DBM.</remarks>
        private int _SMeter;
        public int SMeter
        {
            get
            {
                if (Transmit)
                {
                    // Show forward power = exp(10, (dbm/10)) / 1000
                    return (int)((Math.Pow(10d, (double)(_PowerDBM / 10)) / 1000) + 0.5);
                }
                else
                {
                    if (SmeterInDBM)
                    {
                        return _SMeter;
                    }
                    // return s-units

                    int val = _SMeter + 127 - 3; // puts s0 at 0.
                    if (val < 0) val = 0;
                    int s = val / 6; // S-unit
                    // Perhaps indicate over S9.
                    val = (s <= 9) ? s : val - (9 * 6) + 9;
                    return val;
                }
            }
        }

        public bool HasActiveSlice
        {
            get { return (theRadio.ActiveSlice != null); }
        }

        // Diversity readiness helper; ensure hardware, license, antennas, and slices
        public bool DiversityReady
        {
            get
            {
                if (theRadio == null) return false;
                bool hasHardware = theRadio.DiversityIsAllowed;
                bool hasLicense = (theRadio.FeatureLicense != null) &&
                                  (theRadio.FeatureLicense.LicenseFeatDivEsc != null) &&
                                  theRadio.FeatureLicense.LicenseFeatDivEsc.FeatureEnabled;
                bool hasAntennas = (theRadio.RXAntList != null) && (theRadio.RXAntList.Length >= 2);
                bool hasSlices = theRadio.AvailableSlices >= 2;
                return hasHardware && hasLicense && hasAntennas && hasSlices;
            }
        }

        internal bool DiversityOn
        {
            get
            {
                return HasActiveSlice && theRadio.ActiveSlice.DiversityOn;
            }
            set
            {
                if (!HasActiveSlice) return;
                if (!theRadio.DiversityIsAllowed) return;
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DiversityOn = value; }), "DiversityOn");
            }
        }

        public string DiversityGateMessage
        {
            get
            {
                if (theRadio == null) return "Radio not ready";
                if (!HasActiveSlice) return "Select a slice";
                if (!theRadio.DiversityIsAllowed) return "Model lacks diversity support";
                var licenseFeature = theRadio.FeatureLicense?.LicenseFeatDivEsc;
                if (licenseFeature == null) return "Diversity license status pending";
                if (!licenseFeature.FeatureEnabled)
                {
                    return licenseFeature.FeatureGatedMessage ?? "Purchase a diversity license to enable this feature";
                }
                if ((theRadio.RXAntList?.Length ?? 0) < 2) return "Need two RX antennas";
                if (theRadio.AvailableSlices < 2) return "Need two slices for diversity";
                return string.Empty;
            }
        }

        internal string DiversityStatus
        {
            get
            {
                string gate = DiversityGateMessage;
                if (!string.IsNullOrEmpty(gate)) return gate;
                string status = DiversityOn ? "Diversity active" : "Diversity ready";
                string ants = DiversityAntennas;
                return string.IsNullOrEmpty(ants) ? status : status + " (" + ants + ")";
            }
        }

        private string DiversityAntennas
        {
            get
            {
                if (!HasActiveSlice) return string.Empty;
                string primary = theRadio.ActiveSlice.RXAnt ?? string.Empty;
                string child = theRadio.ActiveSlice.DiversitySlicePartner?.RXAnt;
                if (string.IsNullOrEmpty(child)) return primary;
                if (string.Equals(primary, child, StringComparison.Ordinal)) return primary;
                if (string.IsNullOrEmpty(primary)) return child;
                return primary + "/" + child;
            }
        }

        // a VFO is really a slice index.
        internal Slice VFOToSlice(int vfo)
        {
            Slice rv;
            lock (mySlices)
            {
                rv = (ValidVFO(vfo)) ? mySlices[vfo] : null;
            }
            return rv;
        }

        internal int SliceToVFO(Slice s)
        {
            int rv = noVFO;
            lock (mySlices)
            {
                for (int i = 0; i < mySlices.Count; i++)
                {
                    if (s.Index == mySlices[i].Index)
                    {
                        rv = i;
                        break;
                    }
                }
            }

            if (rv == noVFO)
            {
                Tracing.TraceLine("SliceToVFO:Error", TraceLevel.Error);
            }
            return rv;
        }

        /// <summary>
        /// The next VFO, wraps around.
        /// </summary>
        /// <param name="v">current VFO</param>
        public int NextVFO(int v)
        {
            return (MyNumSlices == 0) ? 0 : (v + 1) % MyNumSlices;
        }

        /// <summary>
        /// The previous VFO, wraps around.
        /// </summary>
        /// <param name="v">current VFO</param>
        public int PriorVFO(int v)
        {
            return (v > 0) ? v - 1 : MyNumSlices - 1;
        }

        // Region rig properties
        #region Rig properties
        private const int noVFO = -1;
        /// <summary>
        /// true if the VFO value is good.
        /// </summary>
        /// <param name="vfo">the VFO</param>
        public bool ValidVFO(int vfo)
        {
            return ((vfo >= 0) & (vfo < MyNumSlices));
        }

        // Note that VFOToSlice(RXVFO) is the ActiveSlice.
        internal int _RXVFO = noVFO;
        public int RXVFO
        {
            get { return _RXVFO; }
            set
            {
                if (_RXVFO != value)
                {
                    _RXVFO = value;
                    if (ValidVFO(value))
                    {
                        q.Enqueue((FunctionDel)(() => { VFOToSlice(value).Active = true; }), "Active");
                        //await(() => { return (_RXVFO == value); }, 1000);
                    }
                    // else we don't reset it.
                }
            }
        }

        internal int _TXVFO = noVFO;
        public int TXVFO
        {
            get { return _TXVFO; }
            set
            {
                if (_TXVFO != value)
                {
                    _TXVFO = value;
                    if (ValidVFO(value))
                    {
                        q.Enqueue((FunctionDel)(() => { mySlices[value].IsTransmitSlice = true; }), "IsTransmitSlice");
                        //await(() => { return (_TXVFO == value); }, 1000);
                    }
                }
            }
        }

        /// <summary>
        /// Get/set the current VFO in use.
        /// </summary>
        public int CurVFO
        {
            get
            {
                return (Transmit) ? TXVFO : RXVFO;
            }
            set
            {
                if (Transmit)
                {
                    TXVFO = value;
                }
                else
                {
                    RXVFO = value;
                }
            }
        }

        /// <summary>
        /// Get the VFO's (slice's) audio
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <returns>true if on</returns>
        public bool GetVFOAudio(int v)
        {
            bool rv;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                rv = !((s != null) ? s.Mute : true);
            }
            return rv;
        }

        /// <summary>
        /// Turn audio on/off.
        /// </summary>
        /// <param name="v">VFO or slice id</param>
        /// <param name="on">true for on</param>
        public void SetVFOAudio(int v, bool on)
        {
            Tracing.TraceLine("SetVFOAudio:" + v + ' ' + on.ToString(), TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.Mute = !on;
                }
            }), "Mute");
        }

        /// <summary>
        /// get the audio pan value
        /// </summary>
        /// <param name="v">VFO or slice</param>
        public int GetVFOPan(int v)
        {
            int rv = (MaxPan - MinPan) / 2;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                if (s != null) rv = s.AudioPan;
            }
            return rv;
        }

        public const int MinPan = 0;
        public const int MaxPan = 100;
        public const int PanIncrement = 10;
        /// <summary>
        /// Adjust the slice audio panning
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <param name="pan">pan value</param>
        public void SetVFOPan(int v, int pan)
        {
            Tracing.TraceLine("SetVFOPan:" + v + ' ' + pan, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.AudioPan = pan;
                }
            }), "AudioPan");
        }

        /// <summary>
        /// get the audio gain value
        /// </summary>
        /// <param name="v">VFO or slice</param>
        public int GetVFOGain(int v)
        {
            int rv = (MaxGain - MinGain) / 2;
            lock (mySlices)
            {
                Slice s = VFOToSlice(v);
                if (s != null) rv = s.AudioGain;
            }
            return rv;
        }

        public const int MinGain = 0;
        public const int MaxGain = 100;
        public const int GainIncrement = 10;
        /// <summary>
        /// Adjust the slice audio gain
        /// </summary>
        /// <param name="v">VFO or slice</param>
        /// <param name="gain">gain value</param>
        public void SetVFOGain(int v, int gain)
        {
            Tracing.TraceLine("SetVFOGain:" + v + ' ' + gain, TraceLevel.Info);
            q.Enqueue((FunctionDel)(() =>
            {
                lock (mySlices)
                {
                    Slice s = VFOToSlice(v);
                    if (s != null) s.AudioGain = gain;
                }
            }), "AudioGain");
        }

        // Can't add/remove VFOs during this.
        public void CopyVFO(int inv, int outv)
        {
            if (Transmit)
            {
                Tracing.TraceLine("CopyVFO:can't be transmitting", TraceLevel.Error);
                return;
            }
            if ((!ValidVFO(inv) | !ValidVFO(outv)) |
                (inv == outv))
            {
                Tracing.TraceLine("CopyVFO:bad VFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Error);
                return;
            }

            Tracing.TraceLine("CopyVFO:" + inv.ToString() + " " + outv.ToString(), TraceLevel.Info);
            Slice inSlice = VFOToSlice(inv);
            Slice outSlice = VFOToSlice(outv);
            q.Enqueue((FunctionDel)null, "slice copy start");
            q.Enqueue((FunctionDel)(() => { outSlice.Freq = inSlice.Freq; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DemodMode = inSlice.DemodMode; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AutoPan = inSlice.AutoPan; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RTTYMark = inSlice.RTTYMark; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RTTYShift = inSlice.RTTYShift; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DIGLOffset = inSlice.DIGLOffset; }));
            q.Enqueue((FunctionDel)(() => { outSlice.DIGUOffset = inSlice.DIGUOffset; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FilterHigh = inSlice.FilterHigh; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FilterLow = inSlice.FilterLow; }));
            q.Enqueue((FunctionDel)(() => { outSlice.ANFOn = inSlice.ANFOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.APFOn = inSlice.APFOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.ANFLevel = inSlice.ANFLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.APFLevel = inSlice.APFLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.WNBOn = inSlice.WNBOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.WNBLevel = inSlice.WNBLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NBOn = inSlice.NBOn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NBLevel = inSlice.NBLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NROn = inSlice.NROn; }));
            q.Enqueue((FunctionDel)(() => { outSlice.NRLevel = inSlice.NRLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCMode = inSlice.AGCMode; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCOffLevel = inSlice.AGCOffLevel; }));
            q.Enqueue((FunctionDel)(() => { outSlice.AGCThreshold = inSlice.AGCThreshold; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMDeviation = inSlice.FMDeviation; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMRepeaterOffsetFreq = inSlice.FMRepeaterOffsetFreq; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMToneValue = inSlice.FMToneValue; }));
            q.Enqueue((FunctionDel)(() => { outSlice.FMTX1750 = inSlice.FMTX1750; }));
            q.Enqueue((FunctionDel)(() => { outSlice.RepeaterOffsetDirection = inSlice.RepeaterOffsetDirection; }));

            List<Slice> sList = new List<Slice>();
            sList.Add(inSlice);
            sList.Add(outSlice);
            q.Enqueue((FunctionDel)(() =>
            {
                FilterObj.RXFreqChange(sList);
            }));
            q.Enqueue((FunctionDel)null, "slice copy done");
        }

        internal double LongFreqToLibFreq(ulong u)
        {
            return (double)u / 1000000d;
        }

        internal ulong LibFreqtoLong(double f)
        {
            return (ulong)(f * 1000000d);
        }

        private ulong _RXFrequency;
        public ulong RXFrequency
        {
            get
            {
                return _RXFrequency;
            }
            set
            {
                _RXFrequency = value;
                if (!ValidVFO(RXVFO))
                {
                    Tracing.TraceLine("RXFrequency: no valid RX slice", TraceLevel.Warning);
                    return;
                }
                var slice = VFOToSlice(RXVFO);
                if (slice == null)
                {
                    Tracing.TraceLine("RXFrequency: RX slice missing", TraceLevel.Warning);
                    return;
                }
                double freq = LongFreqToLibFreq(value);
                q.Enqueue((FunctionDel)(() =>
                {
                    var s = VFOToSlice(RXVFO);
                    if (s != null) s.Freq = freq;
                }), "RXFreq");
            }
        }

        private ulong _TXFrequency;
        public ulong TXFrequency
        {
            get
            {
                return _TXFrequency;
            }
            set
            {
                _TXFrequency = value;
                if (!ValidVFO(TXVFO))
                {
                    Tracing.TraceLine("TXFrequency: no valid TX slice", TraceLevel.Warning);
                    return;
                }
                var slice = VFOToSlice(TXVFO);
                if (slice == null)
                {
                    Tracing.TraceLine("TXFrequency: TX slice missing", TraceLevel.Warning);
                    return;
                }
                double freq = LongFreqToLibFreq(value);
                q.Enqueue((FunctionDel)(() =>
                {
                    var s = VFOToSlice(TXVFO);
                    if (s != null) s.Freq = freq;
                }), "TXFreq");
            }
        }

        /// <summary>
        /// current frequency
        /// </summary>
        public ulong Frequency
        {
            get { return (Transmit) ? TXFrequency : RXFrequency; }
            set
            {
                // Don't set if transmitting.
                if (Transmit)
                {
                    Tracing.TraceLine("Frequency:can't set it now", TraceLevel.Error);
                    return;
                }
                RXFrequency = value;
            }
        }

        /// <summary>
        /// showing XMIT frequency when split.
        /// </summary>
        public bool ShowingXmitFrequency
        {
            get; set;
        }

        /// <summary>
        /// Set frequency according to ShowingXmitFrequency.
        /// </summary>
        public ulong VirtualRXFrequency
        {
            get
            {
                return (ShowingXmitFrequency) ? TXFrequency : RXFrequency;
            }
            set
            {
                if (Transmit) return; // disallow set
                if (ShowingXmitFrequency) TXFrequency = value;
                else RXFrequency = value;
            }
        }

        private string _RXMode = "";
        /// <summary>
        /// RX mode
        /// </summary>
        public string RXMode
        {
            get
            {
                return _RXMode;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.DemodMode = value; }), "RXDemodMode");
            }
        }

        private string _TXMode = "";
        /// <summary>
        /// TX mode
        /// </summary>
        public string TXMode
        {
            get
            {
                return _TXMode;
            }
            set
            {
                if (ValidVFO(TXVFO))
                {
                    q.Enqueue((FunctionDel)(() => { VFOToSlice(TXVFO).DemodMode = value; }), "TXDemodMode");
                }
            }
        }

        /// <summary>
        /// current mode
        /// </summary>
        public string Mode
        {
            get { return (string)((Transmit) ? TXMode : RXMode); }
            set
            {
                // Can't set during transmit.
                if (Transmit) return;
                RXMode = value;
            }
        }

        public int FilterLow
        {
            get
            {
                return theRadio.ActiveSlice.FilterLow;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterLow = value; }), "FilterLow");
            }
        }

        public int FilterHigh
        {
            get
            {
                return theRadio.ActiveSlice.FilterHigh;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FilterHigh = value; }), "FilterHigh");
            }
        }

#if zero
        // TXAntenna must be set first.
        public bool RXAntenna
        {
            get
            {
                return (theRadio.ActiveSlice.RXAnt != VFOToSlice(RXVFO).TXAnt);
            }
            set
            {
                // Use the other antenna, 0 or 1, if true.
                int ant = (value) ? (TXAntenna + 1) % 2 : TXAntenna;
                foreach (Slice s in mySlices)
                {
                    q.Enqueue((FunctionDel)(() => { s.RXAnt = theRadio.RXAntList[ant]; }), "RXAnt");
                }
    }
}

        /// <summary>
        /// Set both the TX and RX antenna values.
        /// </summary>
        public int TXAntenna
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
                    lock (mySlices)
                    {
                        foreach (Slice s in mySlices)
                        {
                            q.Enqueue((FunctionDel)(() => { s.TXAnt = theRadio.RXAntList[value]; }), "TXAnt");
                            q.Enqueue((FunctionDel)(() => { s.RXAnt = theRadio.RXAntList[value]; }), "RXAnt");
                        }
                    }
                }
            }
        }
#endif

        //
        internal const string RxAntDefault = null;
        //private string _RXAntenna;
        internal string RXAntenna
        {
            get
            {
                if (theRadio.ActiveSlice != null) return theRadio.ActiveSlice.RXAnt;
                else return "";
            }
            set
            {
                if (theRadio.ActiveSlice != null)
                {
                    if (value == RxAntDefault)
                    {
                        // same as TXAntenna
                        q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RXAnt = TXAntenna; }), "RXAnt");
                    }
                    else
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RXAnt = value; }), "RXAnt");
                    }
                }
            }
        }

        internal string TXAntenna
        {
            get
            {
                if (theRadio.ActiveSlice != null) return theRadio.ActiveSlice.TXAnt;
                else return "";
            }
            set
            {
                if (theRadio.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.TXAnt = value; }), "TXAnt");
                }
            }
        }

        /// <summary>
        /// Type of the Flex tuner in use
        /// </summary>
        public enum FlexTunerTypes
        {
            none,
            manual,
            auto,
        }

        private FlexTunerTypes _FlexTunerType;
        public FlexTunerTypes FlexTunerType
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

        protected void setFlexTunerTypeNotAuto()
        {
            _FlexTunerType = (MyCaps.HasCap(RigCaps.Caps.ManualATGet)) ?
                FlexTunerTypes.manual : FlexTunerTypes.none;
            Tracing.TraceLine("setFlexTunerTypeNotAuto:" + _FlexTunerType.ToString(), TraceLevel.Info);
        }

        private bool _FlexTunerOn;
        public bool FlexTunerOn
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
                            // Raise status if turning off so we can report the SWR.
                            if (!value)
                            {
                                float highSWR;
                                // Look for minimum SWR.
                                do
                                {
                                    highSWR = _SWR;
                                    Thread.Sleep(100);
                                } while (highSWR > _SWR);
                                // Report status.
                                ATUTuneStatus stat = ATUTuneStatus.OK;
                                RaiseFlexAntTuneStartStop(new FlexAntTunerArg
                                    (FlexTunerType, stat, highSWR));
                            }
                            q.Enqueue((FunctionDel)(() => { theRadio.TXTune = value; }), "TXTune");
                        }
                        break;
                    case FlexTunerTypes.auto:
                        {
                            // Normally tuning stops automatically when finished.
                            q.Enqueue((FunctionDel)(() => { Transmit = value; }), "Transmit");
                            if (value)
                            {
                                q.Enqueue((FunctionDel)(() => { theRadio.ATUTuneStart(); }), "ATUTuneStart");
                            }
                        }
                        break;
                }
                _FlexTunerOn = value;
            }
        }

        public void AntennaTunerMemories()
        {
            Form theForm = new FlexATUMemories(this);
            theForm.ShowDialog();
            theForm.Dispose();
        }

        public bool FlexTunerUsingMemoryNow
        {
            get
            {
                return ((_FlexTunerType == FlexTunerTypes.auto) &
                    (theRadio.ATUTuneStatus != ATUTuneStatus.Bypass) &
                    theRadio.ATUMemoriesEnabled & theRadio.ATUUsingMemory);
            }
        }

        /// <summary>
        /// Per-slice mute toggle. True = muted.
        /// </summary>
        public bool SliceMute
        {
            get
            {
                if (!HasActiveSlice) return false;
                return theRadio.ActiveSlice.Mute;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.Mute = value; }), "SliceMute");
            }
        }

        internal const int AudioGainMinValue = 0;
        internal const int AudioGainMaxValue = 100;
        public int AudioGain
        {
            get
            {
                //return base.AudioGain;
                return theRadio.ActiveSlice.AudioGain;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioGain = value; }), "AudioGain");
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
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AudioPan = value; }), "AudioPan");
            }
        }

        /// <summary>
        /// Mute/unmute local audio.
        /// </summary>
        /// <param name="mute">true or false</param>
        public void LocalAudioMute(bool mute)
        {
            Tracing.TraceLine("LocalAudioMute:" + mute.ToString(), TraceLevel.Info);
            maintainAudio = mute; // enforce this
            theRadio.LineoutMute = mute;
            theRadio.HeadphoneMute = mute;
            theRadio.FrontSpeakerMute = mute;
        }

        internal const int LineoutGainMinValue = 0;
        internal const int LineoutGainMaxValue = 100;
        public int LineoutGain
        {
            get
            {
                //return base.LineoutGain;
                return theRadio.LineoutGain;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.LineoutGain = value; }), "LineoutGain");
            }
        }

        internal const int HeadphoneGainMinValue = 0;
        internal const int HeadphoneGainMaxValue = 100;
        public int HeadphoneGain
        {
            get
            {
                return theRadio.HeadphoneGain;
            }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.HeadphoneGain = value; }), "HeadphoneGain");
            }
        }

        public OffOnValues Vox
        {
            get
            {
                bool val = false;
                if (ValidVFO(TXVFO))
                {
                    if (VFOToSlice(TXVFO).DemodMode == "CW") val = theRadio.CWBreakIn;
                    else val = theRadio.SimpleVOXEnable;
                }
                return (val) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                Slice s = VFOToSlice(TXVFO); // also tests the VFO
                bool val = (value == OffOnValues.on) ? true : false;
                if (s != null)
                {
                    if (s.DemodMode == "CW")
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.CWBreakIn = val; }), "BreakIn");
                    }
                    else
                    {
                        q.Enqueue((FunctionDel)(() => { theRadio.SimpleVOXEnable = val; }), "SimpleVOXEnable");
                    }
                }
            }
        }

        public OffOnValues NoiseBlanker
        {
            get
            {
                return (theRadio.ActiveSlice.NBOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBOn = (value == OffOnValues.on) ? true : false; }), "NBOn");
            }
        }

        // The values are the same for the wide band NB.
        internal const int NoiseBlankerValueMin = 0;
        internal const int NoiseBlankerValueMax = 100;
        internal const int NoiseBlankerValueIncrement = 5;
        internal int NoiseBlankerLevel
        {
            get { return theRadio.ActiveSlice.NBLevel; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NBLevel = value; }), "NBLevel"); }
        }

        public OffOnValues WidebandNoiseBlanker
        {
            get
            {
                return (theRadio.ActiveSlice.WNBOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBOn = (value == OffOnValues.on) ? true : false; }), "WNBOn");
            }
        }

        internal int WidebandNoiseBlankerLevel
        {
            get { return theRadio.ActiveSlice.WNBLevel; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.WNBLevel = value; }), "WNBLevel"); }
        }

        public OffOnValues NoiseReduction
        {
            get
            {
                return (theRadio.ActiveSlice.NROn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NROn = (value == OffOnValues.on) ? true : false; }));
            }
        }

        internal const int NoiseReductionValueMin = 0;
        internal const int NoiseReductionValueMax = 100;
        internal const int NoiseReductionValueIncrement = 5;
        internal int NoiseReductionLevel
        {
            get { return theRadio.ActiveSlice.NRLevel; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLevel = value; })); }
        }

        // Advanced Noise Reduction algorithms (FlexLib v4.0.1)
        // Legacy LMS Noise Reduction (NRL)
        internal const int NoiseReductionLegacyValueMin = 0;
        internal const int NoiseReductionLegacyValueMax = 100;
        internal const int NoiseReductionLegacyValueIncrement = 5;
        public OffOnValues NoiseReductionLegacy
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.NRLOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRLOn = (value == OffOnValues.on); }), "NRLOn");
                }
            }
        }
        internal int NoiseReductionLegacyLevel
        {
            get { return theRadio.ActiveSlice.NRL_Level; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRL_Level = value; }), "NRL_Level"); }
        }

        // Spectral Subtraction Noise Reduction (NRS)
        internal const int SpectralNoiseReductionValueMin = 0;
        internal const int SpectralNoiseReductionValueMax = 100;
        internal const int SpectralNoiseReductionValueIncrement = 5;
        public OffOnValues SpectralNoiseReduction
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.NRSOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSOn = (value == OffOnValues.on); }), "NRSOn");
                }
            }
        }
        internal int SpectralNoiseReductionLevel
        {
            get { return theRadio.ActiveSlice.NRSLevel; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRSLevel = value; }), "NRSLevel"); }
        }

        // Noise Reduction with Filter (NRF)
        internal const int NoiseReductionFilterValueMin = 0;
        internal const int NoiseReductionFilterValueMax = 100;
        internal const int NoiseReductionFilterValueIncrement = 5;
        public OffOnValues NoiseReductionFilter
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.NRFOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFOn = (value == OffOnValues.on); }), "NRFOn");
                }
            }
        }
        internal int NoiseReductionFilterLevel
        {
            get { return theRadio.ActiveSlice.NRFLevel; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.NRFLevel = value; }), "NRFLevel"); }
        }

        // Neural noise reduction (RNN) - toggle only
        public OffOnValues NeuralNoiseReduction
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.RNNOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RNNOn = (value == OffOnValues.on); }), "RNNOn");
                }
            }
        }

        // FFT-based Auto Notch Filter (ANFT) - toggle only
        public OffOnValues AutoNotchFFT
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.ANFTOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFTOn = (value == OffOnValues.on); }), "ANFTOn");
                }
            }
        }

        // Legacy LMS Auto-Notch Filter (ANFL)
        internal const int AutoNotchLegacyLevelMin = 0;
        internal const int AutoNotchLegacyLevelMax = 100;
        internal const int AutoNotchLegacyLevelIncrement = 10;
        public OffOnValues AutoNotchLegacy
        {
            get
            {
                if (!HasActiveSlice || theRadio?.ActiveSlice == null) return OffOnValues.off;
                return (theRadio.ActiveSlice.ANFLOn) ? OffOnValues.on : OffOnValues.off;
            }
            set
            {
                if (HasActiveSlice && theRadio?.ActiveSlice != null)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLOn = (value == OffOnValues.on); }), "ANFLOn");
                }
            }
        }
        internal int AutoNotchLegacyLevel
        {
            get { return theRadio.ActiveSlice.ANFL_Level; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFL_Level = value; }), "ANFL_Level"); }
        }

        /// <summary>
        /// AGC mode
        /// </summary>
        /// <remarks>Different from AllRadios</remarks>
        internal AGCMode AGCSpeed
        {
            get {
                if (!HasActiveSlice) return AGCMode.None;
                return theRadio.ActiveSlice.AGCMode;
            }
            set {
                if (HasActiveSlice)
                {
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCMode = value; }), "AGCMode");
                }
            }
        }

        internal const int AGCThresholdMin = 0;
        internal const int AGCThresholdMax = 100;
        internal const int AGCThresholdIncrement = 5;
        internal int AGCThreshold
        {
            get { return theRadio.ActiveSlice.AGCThreshold; }
            set { if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.AGCThreshold = value; })); }
        }

        /// <summary>
        /// data type for RIT/XIT.
        /// </summary>
        public class RITData
        {
            public bool Active;
            public int Value; // may be negative
            public RITData()
            {
                Active = false;
                Value = 0;
            }
            public RITData(RITData r)
            {
                Active = r.Active;
                Value = r.Value;
            }
        }
        private RITData _RIT = new RITData();
        public RITData RIT
        {
            get
            {
                lock (_RIT)
                {
                    return _RIT;
                }
            }
            set
            {
                // _RIT set in PropertyChangedHandler
                lock (_RIT)
                {
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITOn = value.Active; }));
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RITFreq = value.Value; }));
                }
            }
        }

        private RITData _XIT = new RITData();
        public RITData XIT
        {
            get
            {
                lock (_XIT)
                {
                    return _XIT; ;
                }
            }
            set
            {
                // _XIT set in PropertyChangedHandler
                lock (_XIT)
                {
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITFreq = value.Value; }));
                    if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.XITOn = value.Active; }));
                }
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
                cfgData.BreakinDelay = value;
                issue7620(true);
                i_BreakinDelay = value;
            }
        }
        private int i_BreakinDelay
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWDelay = value; }), "CWDelay");
                if (cwx != null) q.Enqueue((FunctionDel)(() => { cwx.Delay = value; }), "CWDelay");
            }
        }

        internal const int SidetonePitchMin = 0;
        internal const int SidetonePitchMax = 6000;
        internal const int SidetonePitchIncrement = 50;
        internal int SidetonePitch
        {
            get { return theRadio.CWPitch; }
            set
            {
                cfgData.SidetonePitch = value;
                issue7620(true);
                i_SidetonePitch = value;
            }
        }
        private int i_SidetonePitch
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWPitch = value; }), "CWPitch");
            }
        }

        internal const int SidetoneGainMin = 0;
        internal const int SidetoneGainMax = 100;
        internal const int SidetoneGainIncrement = 5;
        internal int SidetoneGain
        {
            get { return theRadio.TXCWMonitorGain; }
            set
            {
                cfgData.SidetoneGain = value;
                issue7620(true);
                i_SidetoneGain = value;
            }
        }
        private int i_SidetoneGain
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.TXCWMonitorGain = value; }), "TXCWMonitorGain");
#if CWMonitor
                if (useCWMon) q.Enqueue((FunctionDel)(() => { CWMon.Volume = value; }), "CWMonVolume");
#endif
            }
        }

        public enum IambicValues
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
                cfgData.Keyer = _Keyer = value;
                issue7620(true);
                i_Keyer = value;
            }
        }
        private IambicValues i_Keyer
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWIambic = (value == IambicValues.off) ? false : true; }), "CWIambic");
                if (value != IambicValues.off)
                {
                    // Set iambic mode.
                    q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeA = (value == IambicValues.iambicA) ? true : false; }), "CWIambicA");
                    q.Enqueue((FunctionDel)(() => { theRadio.CWIambicModeB = (value == IambicValues.iambicB) ? true : false; }), "CWIambicB");
                }
            }
        }

        private bool _CWReverse;
        internal bool CWReverse
        {
            get { return _CWReverse; }
            set
            {
                cfgData.CWReverse = value;
                issue7620(true);
                i_CWReverse = value;
            }
        }
        private bool i_CWReverse
        {
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
                cfgData.KeyerSpeed = value;
                issue7620(true);
                i_KeyerSpeed = value;
            }
        }
        private int i_KeyerSpeed
        {
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.CWSpeed = value; }));
                if (cwx != null) q.Enqueue((FunctionDel)(() => { cwx.Speed = value; }));
            }
        }

        internal OffOnValues CWL
        {
            get { return (theRadio.CWL_Enabled) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                cfgData.CWLEnabled = val;
                issue7620(true);
                i_CWL = value;
            }
        }
        private OffOnValues i_CWL
        {
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
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

        internal OffOnValues Compander
        {
            get { return (theRadio.CompanderOn) ? OffOnValues.on : OffOnValues.off; }
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

        internal OffOnValues Monitor
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
        private int _XmitPower;
        public int XmitPower
        {
            get
            {
                return _XmitPower;
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
        private int _TunePower;
        public int TunePower
        {
            get
            {
                return _TunePower;
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

        internal OffOnValues ANF
        {
            get { return (theRadio.ActiveSlice.ANFOn) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                if (HasActiveSlice)
                {
                    bool val = (value == OffOnValues.on) ? true : false;
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFOn = val; }));
                }
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
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.ANFLevel = value; }));
            }
        }

        public OffOnValues APF
        {
            get { return (theRadio.ActiveSlice.APFOn) ? OffOnValues.on : OffOnValues.off; }
            //get { return (theRadio.APFMode) ? OffOnValues.on : OffOnValues.off; }
            set
            {
                bool val = (value == OffOnValues.on) ? true : false;
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFOn = val; }));
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
                if (HasActiveSlice) q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.APFLevel = value; }));
            }
        }

        private Panadapter activePan
        {
            get { return (theRadio.ActiveSlice != null) ? theRadio.ActiveSlice.Panadapter : null; }
        }

        internal int RFGainMin = -10;
        internal int RFGainMax = 30;
        internal int RFGainIncrement = 10;
        internal int RFGain
        {
            get { return (activePan != null) ? activePan.RFGain : 0; }
            set
            {
                if (activePan != null)
                {
                    q.Enqueue((FunctionDel)(() => { activePan.RFGain = value; }));
                }
            }
        }

#if zero
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

        /// <summary>
        /// Offset direction values
        /// </summary>
        public enum OffsetDirections : byte
        {
            off, minus, plus, allTypes
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
        public OffsetDirections OffsetDirection
        {
            get
            {
                OffsetDirections rv = OffsetDirections.off;
                if (theRadio.ActiveSlice != null)
                {
                    rv = FlexOffsetDirectionToOffsetDirection(theRadio.ActiveSlice.RepeaterOffsetDirection);
                }
                return rv;
            }
            set
            {
                if (theRadio.ActiveSlice != null)
                {
                    FMTXOffsetDirection val = OffsetDirectionToFlexOffsetDirection(value);
                    q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.RepeaterOffsetDirection = val; }));
                }
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
        public int OffsetFrequency
        {
            get { return (int)(theRadio.ActiveSlice.FMRepeaterOffsetFreq * 1e3); }
            set
            {
                q.Enqueue((FunctionDel)(() => { theRadio.ActiveSlice.FMRepeaterOffsetFreq = (double)value / 1e3; }));
            }
        }

        // Valid FM tone modes for this rig, see FMToneMode in Memory.cs in FlexLib.
        /// <summary>
        /// Tone/CTSS value type
        /// </summary>
        public class ToneCTCSSValue
        {
            internal char value;
            private string name;
            internal ToneCTCSSValue(char val, string nam)
            {
                value = val;
                name = nam;
            }
            internal ToneCTCSSValue(char c)
            {
                value = c;
                name = "";
            }
            public static bool operator ==(ToneCTCSSValue val1, ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return true;
                if (((object)val1 == null) || ((object)val2 == null)) return false;
                return (val1.value == val2.value);
            }
            public static bool operator !=(ToneCTCSSValue val1, ToneCTCSSValue val2)
            {
                if (((object)val1 == null) && ((object)val2 == null)) return false;
                if (((object)val1 == null) || ((object)val2 == null)) return true;
                return (val1.value != val2.value);
            }
            public override bool Equals(object obj)
            {
                bool rv;
                try { rv = (value == ((ToneCTCSSValue)obj).value); }
                catch (Exception ex)
                {
                    Tracing.TraceLine("ToneCTCSSValue exception:" + ex.Message, TraceLevel.Error);
                    rv = false;
                }
                return rv;
            }
            public override int GetHashCode()
            {
                return (int)value;
            }
            public override string ToString()
            {
                return name;
            }
        }

        /// <summary>
        /// FM Tone modes
        /// </summary>
        public ToneCTCSSValue[] FMToneModes;

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
        public ToneCTCSSValue ToneCTCSS
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
        public float ToneFrequency
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

        private OffOnValues _DAXOnOff;
        /// <summary>
        /// DAX audio, on or off.
        /// </summary>
        public OffOnValues DAXOnOff
        {
            get { return _DAXOnOff; }
            set
            {
                _DAXOnOff = value;
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

        /// <summary>
        /// Rig's info (list of strings)
        /// </summary>
        public List<string> RigInfo
        {
            get
            {
                List<string> rv = new List<string>();
                rv.Add("Model:" + theRadio.Model);
                rv.Add("Version:" +
                    ((theRadio.Version & 0x00ff000000000000) / 0x0001000000000000).ToString() + '.' +
                    ((theRadio.Version & 0x0000ff0000000000) / 0x0000010000000000).ToString() + '.' +
                    ((theRadio.Version & 0x000000ff00000000) / 0x0000000100000000).ToString()
                    //((theRadio.Version & 0x00000000ffffffff)).ToString();
                    );
                rv.Add("Serial:" + theRadio.Serial);
                rv.Add("Call:" + theRadio.Callsign);
                rv.Add("Nickname:" + theRadio.Nickname);
                rv.Add("IP:" + theRadio.IP.ToString());
                return rv;
            }
        }

        /// <summary>
        /// List of connected stations
        /// </summary>
        public List<string> Stations
        {
            get
            {
                List<string> rv = new List<string>();
                rv.AddRange(theRadio.GuiClientStations.Split(new char[] { ',' }));
                return rv;
            }
        }

        public virtual void TestRoutine()
        {
            Tracing.TraceLine("TestRoutine", TraceLevel.Info);
            MessageBox.Show(RXFrequency.ToString() + ' ' + TXFrequency.ToString(), "msg", MessageBoxButtons.OK);
        }
        #endregion

        // region profile management
        #region profiles
        /// <summary>
        /// Get profiles by type.
        /// </summary>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetProfilesByType(ProfileTypes typ, List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (p.ProfileType == typ)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Get profile by name.
        /// </summary>
        /// <param name="name">name</param>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public Profile_t GetProfileByName(string name, ProfileTypes typ,
            List<Profile_t> lst = null)
        {
            Profile_t rv = null;
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in GetProfilesByType(typ, lst))
            {
                if (p.Name == name)
                {
                    rv = p;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Get default profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetDefaultProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            // Any default profile must be in Callouts.Profiles.
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (p.Default)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

#if zero
        /// <summary>
        /// Get current profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetCurrentProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (Profile_t.Current(this, p))
                {
                    rv.Add(p);
                }
            }
            return rv;
        }
#endif

        /// <summary>
        /// Get all profiles on the radio not in provided list.
        /// </summary>
        /// <param name="lst">list of profiles )may be null)</param>
        public List<Profile_t> GetRigProfiles(List<Profile_t> lst)
        {
            Tracing.TraceLine("GetRigProfiles", TraceLevel.Info);
            List<Profile_t> rv = new List<Profile_t>();
            // Add any profiles not in the list.
            foreach (string name in theRadio.ProfileDisplayList)
            {
                // if no list or else display profile not in it
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.display, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.display,
                        (theRadio.ProfileDisplaySelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileGlobalList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.global, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.global,
                        (theRadio.ProfileGlobalSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileMICList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.mic, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.mic,
                        (theRadio.ProfileMICSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileTXList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.tx, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.tx,
                        (theRadio.ProfileTXSelection == name));
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Select the profile.
        /// </summary>
        /// <param name="prof">the profile</param>
        public bool SelectProfile(Profile_t prof)
        {
            Tracing.TraceLine("SelectProfile:" + prof.ToString(), TraceLevel.Info);
            bool rv = true;
            // select profiles, allowed before main loop.
            string str = "";
            switch (prof.ProfileType)
            {
                case ProfileTypes.display:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileDisplaySelection = prof.Name;
                    }), "ProfileDisplaySelection", true);
                    break;
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileGlobalSelection = prof.Name;
                    }), "ProfileGlobalSelection", true);
                    break;
                case ProfileTypes.tx:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            if (!theRadio.ProfileTXList.Contains(prof.Name))
                            {
                                theRadio.CreateTXProfile(prof.Name);
                                str += "CreateTXProfile_";
                            }
                            theRadio.ProfileTXSelection = prof.Name;
                            str += "ProfileTXSelection";
                        }), str, true);
                    }
                    break;
                case ProfileTypes.mic:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            if (!theRadio.ProfileMICList.Contains(prof.Name))
                            {
                                theRadio.CreateMICProfile(prof.Name);
                                str += "CreateMICProfile_";
                            }
                            theRadio.ProfileMICSelection = prof.Name;
                            str += "ProfileMICSelection";
                        }), str, true);
                    }
                    break;
                default:
                    Tracing.TraceLine("SelectProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    rv = false;
                    break;
            }
            return rv;
        }

        /// <summary>
        /// Save a global profile.
        /// </summary>
        /// <param name="p">the profile</param>
        /// <param name="immediately">(optional)true to save immediately, default is false</param>
        public bool SaveProfile(Profile_t p, bool immediately = false)
        {
            Tracing.TraceLine("SaveProfile:" + p.ToString(), TraceLevel.Info);
            bool commandDone = false;
            bool rv = false;
            if (p.ProfileType == ProfileTypes.global)
            {
                q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.SaveGlobalProfile(p.Name);
                        commandDone = true;
                    }), "save global", true);
                if (immediately)
                {
                    // await the command.
                    await(() =>
                    {
                        return commandDone;
                    }, 3000);
                }
                rv = true;
                // no need to save the newGlobalProfile.
                if (p.Name == newGlobalProfile) newGlobalProfile = null;
            }
            return rv;
        }

        private bool saveNewGlobalProfile()
        {
            Tracing.TraceLine("saveNewGlobalProfile", TraceLevel.Info);
            bool rv = false;
            List<Profile_t> crnt = GetProfilesByType(ProfileTypes.global, GetDefaultProfiles());
            foreach (Profile_t p in crnt)
            {
                if (!string.IsNullOrEmpty(newGlobalProfile) && (p.Name == newGlobalProfile))
                {
                    SaveProfile(p, true);
                    rv = true;
                    break;
                }
            }
            // Don't save other profiles.
            return rv;
        }

        public bool DeleteProfile(Profile_t prof, List<Profile_t> lst = null)
        {
            Tracing.TraceLine("DeleteProfile:" + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Info);
            bool rv = false;
            bool profileGone = false;
            if (lst == null) lst = Callouts.Profiles;
            switch (prof.ProfileType)
            {
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteGlobalProfile(prof.Name);
                        // await the deletion.  profileGone is true if deleted.
                        if (!(profileGone = await(() => { return !theRadio.ProfileGlobalList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteGlobalProfile");
                    break;
                case ProfileTypes.tx:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteTXProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileTXList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteTXProfile");
                    break;
                case ProfileTypes.mic:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteMICProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileMICList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteMICProfile");
                    break;
                default:
                    Tracing.TraceLine("DeleteProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    break;
            }

            // wait for the queued deletion.
            rv = await(() => { return profileGone; }, 1000);

            Profile_t p = GetProfileByName(prof.Name, prof.ProfileType);
            if (rv & (p != null))
            {
                lst.Remove(p);
            }
            return rv;
        }
        #endregion

        // multi-user region
        #region multiUser
        private int initialFreeSlices = -1;
        private int _TotalNumSlices;
        /// <summary>
        /// Total panadapters and slices on the radio.
        /// </summary>
        public int TotalNumSlices
        {
            get { return _TotalNumSlices; }
        }

        internal List<Slice> mySlices = new List<Slice>();

        /// <summary>
        /// number of Panadapters and slices for this radio instance.
        /// </summary>
        public int MyNumSlices
        {
            get {
                int rv;
                lock (mySlices)
                {
                    rv = mySlices.Count;
                }
                return rv;
            }
        }

        /// <summary>
        /// number of slices used by others.
        /// </summary>
        public int OtherNumSlices
        {
            get
            {
                return theRadio.SliceList.Count - MyNumSlices;
            }
        }

        public enum SliceStates
        {
            none,
            mine,
            others,
            available
        }

        public SliceStates SliceState(int id)
        {
            SliceStates rv = SliceStates.none;
            if (id < MyNumSlices) rv = SliceStates.mine;
            else if (id - MyNumSlices < OtherNumSlices) rv = SliceStates.others;
            else rv = SliceStates.available;
            return rv;
        }

        /// <summary>
        /// Add a pan and slice.
        /// </summary>
        public bool NewSlice()
        {
            Tracing.TraceLine("NewSlice:", TraceLevel.Info);
            if (MyNumSlices == TotalNumSlices) return false;

            int myRXVFO = RXVFO;
            int myTXVFO = TXVFO;
            mySliceAdded = false; // need to know when slice added.
            q.Enqueue((FunctionDel)(() =>
            {
                theRadio.RequestPanafall();
                if (await(() =>
                {
                    // await both slice and panadapter.
                    return mySliceAdded & (MyNumPanadapters == MyNumSlices);
                }, 3000))
                {
                    // restore VFOs.
                    RXVFO = myRXVFO;
                    if (CanTransmit) TXVFO = myTXVFO;
                }
                else
                {
                    Tracing.TraceLine("NewSlice:counts don't match", TraceLevel.Error);
                }
            }));
            return true;
        }

        /// <summary>
        /// Remove a pan and slice.
        /// </summary>
        /// <param name="id">slice index</param>
        /// <returns>true if id valid</returns>
        public bool RemoveSlice(int id)
        {
            if ((id < 0) | (id > MyNumSlices)) return false;
            // Can't remove the active or transmit VFO.
            if ((id == RXVFO) | (CanTransmit & (id == TXVFO))) return false;

            Slice slc;
            Panadapter pan;
            lock (mySlices)
            {
                slc = mySlices[id];
            }
            pan = slc.Panadapter;

            q.Enqueue((FunctionDel)(() => { slc.Close(); ; }));
            q.Enqueue((FunctionDel)(() => { pan.Close(); }));
            return true;
        }

        /// <summary>
        /// true if can transmit (currently unused)
        /// </summary>
        public bool CanTransmit { get; internal set; }

        /// <summary>
        /// True if the only station.
        /// </summary>
        public bool OnlyStation { get; internal set; }

        public delegate void NoSliceErrorDel(object sender, string msg);
        /// <summary>
        /// No slice allocated to this instance.
        /// </summary>
        public event NoSliceErrorDel NoSliceError;
        private void raiseNoSliceError(string msg)
        {
            Tracing.TraceLine("raiseNoSliceError:" + msg, TraceLevel.Error);
            if (NoSliceError != null)
            {
                NoSliceError(this, msg);
            }
        }

        private bool _LocalPTT;
        /// <summary>
        /// True if local PTT, can only be set to true.
        /// </summary>
        public bool LocalPTT
        {
            get { return _LocalPTT; }
            set
            {
                if ((value != _LocalPTT) & value)
                {
                    _LocalPTT = value;
                    q.Enqueue((FunctionDel)(() => { theRadio.SetLocalPttForGuiClient(); }));
                }
            }
        }
        #endregion

        // region remote audio
        #region RemoteAudio
        // Note that here input and output refer to input and output from the rig.
        private JJPortaudio.Devices audioSystem;
        private JJPortaudio.Devices.Device remoteInputDevice, remoteOutputDevice;
        private const uint opusSampleRate = 48000;

        class audioChannelData
        {
            public string Name;
            private object radioStream; // the radio's stream
            // OpusStream for output
            public RXRemoteAudioStream OpusChannel
            {
                get { return (RXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            // opus input
            public TXRemoteAudioStream TXOpusChannel
            {
                get { return (TXRemoteAudioStream)radioStream; }
                set { radioStream = value; }
            }
            public bool IsOpus;
            public bool IsInput;
            public JJAudioStream PortAudioStream;
            public bool Started;
            public bool JustStarted; // used by opus to ignore initial data after start

            // audioChannel for Opus output
            public audioChannelData(RXRemoteAudioStream stream, string name)
            {
                stream.IsCompressed = true;
                OpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = false;
            }
            // audioChannel for Opus input
            public audioChannelData(TXRemoteAudioStream stream, string name)
            {
                TXOpusChannel = stream;
                Name = name;
                IsOpus = true;
                IsInput = true;
            }
        }
        private audioChannelData opusOutputChannel;
        private audioChannelData opusInputChannel;
#if CWMonitor
        private Morse CWMon = null;
        private bool useCWMon { get { return (CWMon != null); } }
#endif

        // for Opus output
        private RXRemoteAudioStream rxStream = null;
        private void opusOutputStreamAddedHandler(RXRemoteAudioStream stream)
        {
            if (!myClient(stream.ClientHandle))
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:not mine", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("opusOutputStreamAddedHandler:mine", TraceLevel.Info);
                rxStream = stream;
            }
        }

        private TXRemoteAudioStream txStream = null;
        private void opusInputStreamAddedHandler(TXRemoteAudioStream stream)
        {
            Tracing.TraceLine("opusInputStreamAddedHandler:" + stream.ClientHandle + ' ' + stream.StreamID.ToString(), TraceLevel.Info);
            txStream = stream;
        }

        private Thread remoteAudioThread = null;
        private bool stopRemoteAudio;

        private bool _PCAudio;
        /// <summary>
        /// Audio over PC
        /// </summary>
        public bool PCAudio
        {
            get { return _PCAudio; }
            set
            {
                Tracing.TraceLine("PCAudio:" + value.ToString(), TraceLevel.Info);
                if (_PCAudio != value)
                {
                    if (value)
                    {
                        startRemoteAudioThread();
                    }
                    else
                    {
                        stopRemoteAudioThread();
                    }
                    _PCAudio = value;
                }
            }
        }

        private void startRemoteAudioThread()
        {
            Tracing.TraceLine("startRemoteAudioThread", TraceLevel.Info);
            stopRemoteAudio = false;
            remoteAudioThread = new Thread(remoteAudioProc);
            remoteAudioThread.Name = "RemoteAudio";
            remoteAudioThread.Priority = ThreadPriority.Highest;
            remoteAudioThread.Start();
        }

        private void stopRemoteAudioThread()
        {
            if (PCAudio & !stopRemoteAudio)
            {
                Tracing.TraceLine("stopRemoteAudioThread", TraceLevel.Info);
                try
                {
                    stopRemoteAudio = true;
                    if (!remoteAudioThread.Join(6000))
                    {
                        Tracing.TraceLine("stopRemoteAudioThread must abort", TraceLevel.Error);
                        remoteAudioThread.Abort();
                    }
                }
                catch(Exception ex)
                {
                    Tracing.TraceLine("stopRemoteAudioThread exception:" + ex.Message, TraceLevel.Error);
                }
                remoteAudioThread = null;
            }
        }

        private void remoteAudioProc()
        {
            Tracing.TraceLine("remoteAudioProc is WAN=" + RemoteRig.ToString(), TraceLevel.Info);
            opusOutputChannel = null;
            opusInputChannel = null;
#if CWMonitor
            CWMon = null;
#endif

#if zero
            // input is from pc.
            string oldMicInput = theRadio.MicInput;
            theRadio.MicInput = "PC";
            if (!await(() =>
            {
                return theRadio.RemoteTxOn;
            }, 1000))
            {
                Tracing.TraceLine("remoteAudioProc:remote tx should be on", TraceLevel.Error);
            }
#endif

            audioSystem = new JJPortaudio.Devices(Callouts.AudioDevicesFile);
            // Get the configured devices.
            if (!audioSystem.Setup())
            {
                Tracing.TraceLine("remoteAudioProc:audio setup failed", TraceLevel.Error);
                goto remoteDone;
            }
            remoteInputDevice =
                audioSystem.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.input, true);
            if (remoteInputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteInputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }
            remoteOutputDevice =
                audioSystem.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.output, true);
            if (remoteOutputDevice == null)
            {
                Tracing.TraceLine("remoteAudioProc:remoteOutputDevice setup error", TraceLevel.Error);
                goto remoteDone;
            }

            // Start the audio subsystem.
            // Note: We're not using DAX any more.
            JJPortaudio.Audio.Initialize(remoteInputDevice, remoteOutputDevice);

            // Setup audio channels, output first.
            rxStream = null;
            theRadio.RequestRXRemoteAudioStream(true); // see opusOutputStreamAddedHandler
            if (!await(() =>
                {
                    return (rxStream != null);
                }, 10000))
            {
                Tracing.TraceLine("remoteAudioProc: opus output channel not added.", TraceLevel.Error);
                goto remoteDone;
            }
            theRadio.IsMuteLocalAudioWhenRemoteOn = true;
            opusOutputChannel = new audioChannelData(rxStream, "JJFlexRadio.OpusOutputChan");
            opusOutputChannel.PortAudioStream = new JJAudioStream();
            opusOutputChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.output, opusSampleRate);
            // Boost Opus output to compensate for low remote audio levels.
            // The Opus decode path bypasses FlexLib's RXGain scalar, so decoded audio
            // is at raw codec level which is typically too quiet for laptop speakers.
            // Raw Opus peaks ~0.16. 4x = comfortable, 6x = clean, 8x = hot.
            // Default 4x; user can adjust via Settings > Audio Boost menu.
            opusOutputChannel.PortAudioStream.OutputGain = 4.0f;
            Tracing.TraceLine("remoteAudioProc:opusOutputChannel:" + opusOutputChannel.Name + " setup, OutputGain=" + opusOutputChannel.PortAudioStream.OutputGain, TraceLevel.Info);

            if (!startOpusOutputChannel())
            {
                Tracing.TraceLine("remoteAudioProc: opus output channel not started.", TraceLevel.Error);
                goto remoteDone;
            }

            // Setup the transmit audio, after the rx audio, but don't start the I/O.
            txStream = null;
            theRadio.RequestRemoteAudioTXStream(); // see opusInputStreamAddedHandler
            if (!await(() =>
                {
                    return (txStream != null);
                }, 10000))
            {
                Tracing.TraceLine("remoteAudioProc: didn't get RemoteAudioTXStream from radio", TraceLevel.Error);
                goto remoteDone;
            }
            opusInputChannel = new audioChannelData(txStream, "JJFlexRadio.OpusInputChan");
            opusInputChannel.PortAudioStream = new JJAudioStream();
            opusInputChannel.PortAudioStream.OpenOpus(Devices.DeviceTypes.input, opusSampleRate, sendOpusInput);
            Tracing.TraceLine("remoteAudioProc:Opus Input Channel setup", TraceLevel.Info);

#if CWMonitor
            // Also need a cw monitor
            CWMonInit();
#endif

            // Main audio loop.
            // Note that we must pole for opus output.
            while (!stopRemoteAudio)
            {
                // Just spin if disconnecting.
                if (Disconnecting) continue;

                string mode = "";
                lock (mySlices)
                {
                    Slice s = VFOToSlice(TXVFO);
                    if (s != null) mode = s.DemodMode;
                }
                if (mode != "CW")
                {
                    if (Transmit)
                    {
                        startOpusInputChannel(); // only starts it once
                    }
                    else
                    {
                        stopOpusInputChannel(); // only stops it once.
                    }
                }

                // opus receive polling.
                // get opus data, even during transmit (for QSK).
                byte[] opusBuf = null;
                lock (opusOutputChannel)
                {
                    RXAudioStream stream = opusOutputChannel.OpusChannel;
                    lock (stream.OpusRXListLockObj)
                    {
                        int lastID = stream._opusRXList.Count - 1;
                        // ignore initial packets.
                        if (opusOutputChannel.JustStarted)
                        {
                            opusOutputChannel.JustStarted = false;
                            stream.LastOpusTimestampConsumed = stream._opusRXList.Keys[lastID];
                        }
                        else
                        {
                            for (int i = 0; i < stream._opusRXList.Count; i++)
                            {
                                if (stream.LastOpusTimestampConsumed <
                                    stream._opusRXList.Keys[i])
                                {
                                    opusBuf = stream._opusRXList.Values[i].payload;
                                    stream.LastOpusTimestampConsumed = stream._opusRXList.Keys[i];
                                    break;
                                }
                            }
                        }
                    }
                }
                if (opusBuf != null)
                {
                    opusOutputChannel.PortAudioStream.WriteOpus(opusBuf);
#if opusToFile
                    writeOpus(opusBuf);
#endif
                }
                else
                {
                    Thread.Yield();
                }
            }

            Tracing.TraceLine("remoteAudioProc:stopping remote audio", TraceLevel.Info);

            remoteDone:
            // Note that theRadio may be null here.
#if opusToFile
            closeOpus();
#endif
#if CWMonitor
            if (useCWMon) CWMonDone();
#endif

            if (opusOutputChannel != null)
            {
                stopOpusOutputChannel();
                if (opusOutputChannel.PortAudioStream != null)
                {
                    opusOutputChannel.PortAudioStream.Close();
                    opusOutputChannel.PortAudioStream = null;
                }
                if (opusOutputChannel.OpusChannel != null)
                {
                    opusOutputChannel.OpusChannel.Close();
                    opusOutputChannel.OpusChannel = null;
                    rxStream = null;
                }
            }

            if (opusInputChannel != null)
            {
                stopOpusInputChannel();
                if (opusInputChannel.PortAudioStream != null)
                {
                    opusInputChannel.PortAudioStream.Close();
                    opusInputChannel.PortAudioStream = null;
                }
                if (opusInputChannel.TXOpusChannel != null)
                {
                    opusInputChannel.TXOpusChannel.Close();
                    opusInputChannel.TXOpusChannel = null;
                    txStream = null;
                }
            }

            Audio.Terminate();
            opusOutputChannel = null;
            opusInputChannel = null;
#if CWMonitor
            CWMon = null;
#endif
#if zero
            // Restore mic input.
            theRadio.MicInput = oldMicInput;
#endif

            Tracing.TraceLine("remoteAudioProc exiting", TraceLevel.Info);
        }

        // Note:  Called from the audio callback.
        private void sendOpusInput(byte[] data)
        {
#if opusInputToFile
            opusIn_writeOpus(data);
#endif
            if (data.Length > 0)
            {
                opusInputChannel.TXOpusChannel.AddTXData(data);
            }
            else { }
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
#if opusInputToFile
        private const string opusIn_fName = @"c:\users\jjs\documents\tmp\opusOut.dat";
        private Stream opusIn_fStream = null;
        private BinaryWriter opusIn_fbw = null;
        private void opusIn_writeOpus(byte[] buf)
        {
            if (opusIn_fStream == null)
            {
                opusIn_fStream = File.Open(opusIn_fName, FileMode.Create);
                opusIn_fbw = new BinaryWriter(opusIn_fStream);
            }
            opusIn_fbw.Write((ushort)buf.Length);
            opusIn_fbw.Write(buf, 0, buf.Length);
        }
        private void opusIn_closeOpus()
        {
            if (opusIn_fStream != null)
            {
                opusIn_fbw.Close();
                opusIn_fbw.Dispose();
                opusIn_fStream.Dispose();
            }
        }
#endif

        private string oldMicInput;
        private bool startOpusOutputChannel()
        {
            Tracing.TraceLine("startOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.JustStarted = true; // set on each call
                opusOutputChannel.OpusChannel.RxMute = false;
                if (opusOutputChannel.Started) return true;
                oldMicInput = theRadio.MicInput;
                theRadio.MicInput = "PC";
                opusOutputChannel.OpusChannel.RXGain = 50;
                opusOutputChannel.Started = opusOutputChannel.PortAudioStream.StartAudio();
                if (!opusOutputChannel.Started)
                {
                    Tracing.TraceLine("startOpusOutputChannel portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusOutputChannel.Started;
        }

        private void stopOpusOutputChannel()
        {
            Tracing.TraceLine("stopOpusOutputChannel:" +
                opusOutputChannel.Name + ' ' + opusOutputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusOutputChannel)
            {
                opusOutputChannel.OpusChannel.RxMute = true;
                if (!opusOutputChannel.Started) return;
                try
                {
                    theRadio.MicInput = oldMicInput;
                }
                // ignore error.
                catch { }
                opusOutputChannel.Started = false;
                opusOutputChannel.PortAudioStream.StopAudio();
            }
        }

        private bool startOpusInputChannel()
        {
            Tracing.TraceLine("startOpusInputChannel:" +
                opusInputChannel.Name + ' ' + opusInputChannel.Started.ToString(), TraceLevel.Info);
            lock (opusInputChannel)
            {
                if (opusInputChannel.Started) return true;
                opusInputChannel.Started = opusInputChannel.PortAudioStream.StartAudio();
                if (!opusInputChannel.Started)
                {
                    Tracing.TraceLine("startOpusInputChannel portAudio didn't start", TraceLevel.Error);
                }
            }
            return opusInputChannel.Started;
        }

        private void stopOpusInputChannel()
        {
            if (opusInputChannel == null)
            {
                return;
            }
            lock (opusInputChannel)
            {
                if (!opusInputChannel.Started)
                {
                    return;
                }
                Tracing.TraceLine("stopOpusInputChannel:" +
                    opusInputChannel.Name + ' ' + opusInputChannel.Started.ToString(), TraceLevel.Info);
                opusInputChannel.PortAudioStream.StopAudio();
                opusInputChannel.Started = false;
            }
        }

#if CWMonitor
        // Remote CW monitor
        private void CWMonInit()
        {
            Tracing.TraceLine("CWMonInit", TraceLevel.Info);
            CWMon = new Morse();
            CWMonStart();
        }

        private bool CWMonStart()
        {
            Tracing.TraceLine("CWMonStart", TraceLevel.Info);
            lock (CWMon)
            {
                if (CWMon.Start()) // Sets CWMon.Started.
                {
                    CWMon.Frequency = (uint)theRadio.CWPitch;
                    CWMon.Speed = (uint)theRadio.CWSpeed;
                    CWMon.Volume = theRadio.TXCWMonitorGain;
                }
            }
            return CWMon.Started;
        }

        private void CWMonStop()
        {
            Tracing.TraceLine("CWMonStop", TraceLevel.Info);
            lock (CWMon)
            {
                CWMon.Stop();
            }
        }

        private void CWMonDone()
        {
            Tracing.TraceLine("CWMonDone", TraceLevel.Info);
            lock (CWMon)
            {
                CWMonStop();
                CWMon.Close();
            }
        }
#endif
        #endregion

        // region - cw
        #region cw
        private enum cwBufferState
        {
            stop,
            normal,
            buffering
        }

        class cwText
        {
            public string Text;
            public cwBufferState State;

            public cwText() { }

            public cwText(cwBufferState s)
            {
                State = s;
            }

            public cwText(string str)
            {
                State = cwBufferState.normal;
                Text = str;
            }
        }

        private cwText cwBuffer = new cwText();

        public bool CWBuffering
        {
            get
            {
                lock (cwBuffer)
                {
                    return (cwBuffer.State == cwBufferState.buffering);
                }
            }
            set
            {
                lock (cwBuffer)
                {
                    if (value == CWBuffering) return; // no change
                    if (value)
                    {
                        cwBuffer.State = cwBufferState.buffering;
                    }
                    else
                    {
                        string temp = string.Copy(cwBuffer.Text);
                        cwBuffer.State = cwBufferState.normal;
                        cwBuffer.Text = "";
                        SendCW(temp);
                    }
                }
            }
        }

        /// <summary>
        /// Send or buffer cw text.
        /// </summary>
        /// <param name="str">the text string</param>
        public bool SendCW(string str)
        {
            lock (cwBuffer)
            {
                if (CWBuffering)
                {
                    cwBuffer.Text += str;
                }
                else
                {
                    q.Enqueue(new cwText(str), "SendCW");
                }
            }
            return true;
        }

        public bool SendCW(char c)
        {
            if (CWBuffering)
            {
                cwBuffer.Text += c;
            }
            else
            {
                q.Enqueue(c);
            }
            return true;
        }

        /// <summary>
        ///  Immediat cw stop, clears buffer, but leaves buffering on if was on.
        /// </summary>
        public void StopCW()
        {
            lock (cwBuffer)
            {
                cwBuffer.Text = "";
            }
            q.Enqueue(new cwText(cwBufferState.stop), "StopCW");
        }

        public void CWZeroBeat()
        {
            ulong freq = 0;
            freq = FilterObj.ZeroBeatFreq();
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

        #region parms
        /// <summary>
        /// Receive decoded CW.
        /// </summary>
        /// <param name="txt">the text string</param>
        public delegate void DCWText(string txt);
        /// <summary>
        /// Format the frequency for display
        /// </summary>
        /// <param name="freq">a ulong</param>
        /// <returns>string to display</returns>
        public delegate string FormatFreqDel(ulong freq);
        /// <summary>
        /// format a frequency string for the radio.
        /// </summary>
        /// <param name="str"></param>
        /// <returns>a ulong frequency</returns>
        public delegate ulong FormatFreqForRadioDel(string str);
        /// <summary>
        /// Get the displayable SWR.
        /// </summary>
        /// <returns>SWR string</returns>
        public delegate string GetSWRTextDel();
        /// <summary>
        /// rig-dependent next value of this field.
        /// </summary>
        public delegate void NextValue1Del();

        /// <summary>
        /// Callout vector
        /// </summary>
        public class OpenParms
        {
            /// <summary>
            /// the program name.
            /// </summary>
            public string ProgramName;
            public DCWText CWTextReceiver { get; set; }
            internal void safeCWTextReceiver(string txt)
            {
                try { CWTextReceiver(txt); }
                catch (Exception ex)
                { Tracing.ErrMessageTrace(ex, false, false); }
            }
            /// <summary>
            /// Format a frequency for display
            /// </summary>
            public FormatFreqDel FormatFreq;
            /// <summary>
            /// format a string frequency for the radio
            /// </summary>
            public FormatFreqForRadioDel FormatFreqForRadio;
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public delegate void GotoHomeDel();
            /// <summary>
            /// Go to the home field.
            /// </summary>
            public GotoHomeDel GotoHome;
            /// <summary>
            /// Configuration directory
            /// </summary>
            public string ConfigDirectory;
            /// <summary>
            /// Name of audio device selection file.
            /// </summary>
            public string AudioDevicesFile;
            public delegate string GetOperatorNameDel();
            /// <summary>
            /// Function to retrieve the current operator's name.
            /// </summary>
            public GetOperatorNameDel GetOperatorName;
            internal string OperatorName { get { return GetOperatorName(); } }
            /// <summary>
            /// Station name
            /// </summary>
            public string StationName;
            /// <summary>
            /// Braille display cells
            /// </summary>
            public int BrailleCells;
            /// <summary>
            /// Operator's license class.
            /// </summary>
            public Bands.Licenses License;
            /// <summary>
            /// Send CW with no preprocessing.
            /// </summary>
            public bool DirectSend;
            /// <summary>
            /// panning field
            /// </summary>
            public Control PanField;
            /// <summary>
            /// Get the displayable SWR.
            /// </summary>
            public GetSWRTextDel GetSWRText = null;
            /// <summary>
            /// rig-dependent next value.
            /// </summary>
            public NextValue1Del NextValue1;
            /// <summary>
            /// List of user's profiles
            /// </summary>
            public List<Profile_t> Profiles;
        }
        /// <summary>
        /// Callout vector provided at open().
        /// </summary>
        internal OpenParms Callouts;
        internal string ConfigDirectory { get { return Callouts.ConfigDirectory; } }
        internal string OperatorName { get { return Callouts.OperatorName; } }
        /// <summary>
        /// Operator's directory for rig-specific stuff.
        /// </summary>
        internal string OperatorsDirectory { get { return ConfigDirectory + "\\" + OperatorName; } }

        // Formatters from callouts.
        internal static FormatFreqDel FormatFreq;

        /// <summary>
        /// handle an operator change
        /// </summary>
        public void OperatorChangeHandler()
        {
            FilterObj.OperatorChangeHandler();
        }
        #endregion

        /// <summary>
        /// Rig's capabilities
        /// </summary>
        public RigCaps MyCaps;

        /// <summary>
        /// True if the connected radio supports CW autotune.
        /// </summary>
        public bool SupportsCwAutotune => MyCaps?.HasCap(RigCaps.Caps.CWAutoTuneSet) == true;

        /// <summary>
        /// Invoke CW autotune on the active slice, if supported.
        /// </summary>
        /// <param name="isIntermittent">Optional intermittent flag (FlexLib slice auto_tune int=)</param>
        public void CWAutotune(bool? isIntermittent = null)
        {
            if (!SupportsCwAutotune) return;
            if (!HasActiveSlice) return;

            try
            {
                theRadio.ActiveSlice.SendCWAutotuneCommand(isIntermittent);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("CWAutotune error: " + ex.Message, TraceLevel.Error);
            }
        }

        protected Flex6300Filters FilterObj;

        internal class q_t
        {
            //private Queue q;
            private BlockingCollection<object> q;
            public q_t()
            {
                //q = Queue.Synchronized(new Queue());
                q = new BlockingCollection<object>();
            }

            public bool MainLoop { get; set; }

            public int Count { get { return q.Count; } }

            internal class QItem_t
            {
                public string Name;
                public object Item;
                public QItem_t(string name, object item)
                {
                    Name = (name == null) ? "unnamed" : name;
                    Item = item;
                }
            }

            public void Enqueue(object o, string name = null, bool beforeMainLoop = false)
            {
                QItem_t item = new QItem_t(name, o);

                if (!MainLoop)
                {
                    // If can execute before the main loop, do it.
                    if (beforeMainLoop & (o is FunctionDel))
                    {
                        FunctionDel func = (FunctionDel)o;
                        DbgTrace("q.Enqueue:" + name);
                        if (func != null) func();
                        DbgTrace("q.Enqueue:done " + name);
                    }
                    else Tracing.TraceLine("q:outside main loop", TraceLevel.Error);
                }
                else
                {
                    q.Add(item);
                }
            }

            public QItem_t Dequeue()
            {
                return (QItem_t)q.Take();
            }
        }
        internal q_t q;

        public FlexBase(OpenParms p)
        {
            Tracing.TraceLine("Flex constructor", TraceLevel.Info);
            theRadio = null;
            _apiInit = false;
            wan = null;

            Callouts = p;
            FormatFreq = p.FormatFreq;
            MyCaps = new RigCaps(RigCaps.DefaultCapsList);
            // default tuner type.
            setFlexTunerTypeNotAuto();

            FMToneModes = myFMToneModes;
            // Use the TS590 fm tone values.
            ToneFrequencyTable = myToneFrequencyTable;

            q = new q_t();

            API.ProgramName = p.ProgramName;
            API.IsGUI = true;

            p.NextValue1 = setNextValue1;
            p.GetSWRText = SWRText;
        }

        // main thread region
        #region mainThread
        private bool stopMainThread;
        internal delegate void FunctionDel();

#if zero
        private const string JJRadioDefault = "JJRadioDefault";
        internal string CurrentProfile; // profile in-use.
        private string preferredProfile = null;
        private List<string> defaultProfiles;
#endif
        private void mainThreadProc()
        {
            Tracing.TraceLine("mainThreadProc", TraceLevel.Info);
#if zero
            defaultProfiles = new List<string>();
            // add in order of preference.
            if (Callouts.Profiles != null)
            {
                foreach(Profile_t p in Callouts.Profiles)
                {
                    if (p.Default)
                    {
                        preferredProfile = p.Name;
                        break;
                    }
                }
            }
            if (preferredProfile == null) preferredProfile = Callouts.StationName + "profile";
            defaultProfiles.Add(preferredProfile);
            defaultProfiles.Add(JJRadioDefault);
#endif

            try
            {
                // If a default global profile, select it and await the pan and slices.
                if (GetProfileInfo(false))
                {
                    Tracing.TraceLine("flex open:got profile", TraceLevel.Info);
                }
                else
                {
                    setupFromScratch();
                }

                // Set these on every open.
                Tracing.TraceLine("flex open:#VFOs " + MyNumSlices, TraceLevel.Info);

                if (!RemoteRig)
                {
                    theRadio.MicInput = "mic";
                }
                if (!await(() =>
                {
                    return !theRadio.RemoteTxOn;
                }, 1000))
                {
                    Tracing.TraceLine("Flex open:remote tx should be off", TraceLevel.Error);
                }

                // Turn the Vox off.
                theRadio.SimpleVOXEnable = false;
                theRadio.CWBreakIn = false;

                // Ok to queue commands now.
                q.MainLoop = true;
                Tracing.TraceLine("flex open:q.mainloop" + q.MainLoop.ToString(), TraceLevel.Info);

                cwx = theRadio.GetCWX();
                cwx.Delay = theRadio.CWDelay;
                cwx.Speed = theRadio.CWSpeed;
                cwx.CharSent += new CWX.CharSentEventHandler(charSentHandler);

                // temporary changes for Flex issue #7620.
                issue7620(false);

                if (RemoteRig & !PCAudio)
                {
                    PCAudio = true;
                }

                // Setup pan adapter display.
                FilterObj.PanSetup();
                FilterObj.RXFreqChange(theRadio.ActiveSlice);

                raisePowerEvent(true);

                // Enable TX1 RCA by default for compatibility.
                theRadio.TX1Enabled = true;

#if KeepAlive
                keepAlive_t keepAlive = new keepAlive_t(this);
#endif

                // Main loop.
                while (!stopMainThread)
                {
#if KeepAlive
                    if (q.Count != 0)
                    {
                        keepAlive.Done();
                        keepAlive = null;
                    }
#endif

                    while (q.Count > 0)
                    {
                        q_t.QItem_t el = q.Dequeue();
                        try
                        {
                            if (el.Item is FunctionDel)
                            {
                                Tracing.TraceLine("mainLoop:" + el.Name, TraceLevel.Info);
                                FunctionDel func = (FunctionDel)el.Item;
                                if (func != null) func();
                                Tracing.TraceLine("mainLoop:done " + el.Name, TraceLevel.Info);
                            }
                            else if (el.Item is cwText)
                            {
                                cwText cwt = (cwText)el.Item;
                                if (cwt.State == cwBufferState.stop)
                                {
                                    stopCW();
                                }
                                else
                                {
                                    sendCWString(cwt.Text);
                                }
                            }
                            else if (el.Item is char)
                            {
                                sendCWChar(((char)el.Item));
                            }
                        }
                        catch (Exception ex)
                        {
                            Tracing.TraceLine("mainLoop exception:" +
                                ex.Message + Environment.NewLine + ex.StackTrace, TraceLevel.Error);
                        }
                    }

#if KeepAlive
                    if (keepAlive == null)
                    {
                        keepAlive = new keepAlive_t(this);
                    }
#endif

                    Thread.Sleep(25);
                    //Thread.Yield();
                }
#if KeepAlive
                if (keepAlive != null)
                {
                    keepAlive.Done();
                    keepAlive = null;
                }
#endif
                q.MainLoop = false;

                raisePowerEvent(false);
            }
            catch (ThreadAbortException) { Tracing.TraceLine("mainThread abort", TraceLevel.Error); }
            catch (Exception ex)
            {
                Tracing.ErrMessageTrace(ex, true);
            }
        }
        public class cfg7620
        {
            public int BreakinDelay = 300;
            public int SidetonePitch = 600;
            public int SidetoneGain = 55;
            public int TXCWMonitorGain = 55;
            public bool CWReverse = false;
            public bool CWLEnabled = false;
            public IambicValues Keyer = IambicValues.off;
            public int KeyerSpeed = 20;
        }
        private cfg7620 cfgData = new cfg7620();
        private void issue7620(bool writeFlag)
        {
            string fileName = OperatorsDirectory + '\\' + "issue7620.xml";
            Stream cfgStream = null;

            if (writeFlag)
            {
                Tracing.TraceLine("issue7620:write", TraceLevel.Info);
                try
                {
                    cfgStream = File.Open(fileName, FileMode.Create);
                    XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                    xs.Serialize(cfgStream, cfgData);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("issue7620:write:exception:" + ex.Message, TraceLevel.Error);
                }
                finally
                {
                    if (cfgStream != null) cfgStream.Dispose();
                }
            }
            else
            {
                Tracing.TraceLine("issue7620:read:" + File.Exists(fileName).ToString(), TraceLevel.Info);
                if (File.Exists(fileName))
                {
                    try
                    {
                        cfgStream = File.Open(fileName, FileMode.Open);
                        XmlSerializer xs = new XmlSerializer(typeof(cfg7620));
                        cfgData = (cfg7620)xs.Deserialize(cfgStream);
                        i_BreakinDelay = cfgData.BreakinDelay;
                        i_SidetoneGain = cfgData.SidetoneGain;
                        i_SidetonePitch = cfgData.SidetonePitch;
                        i_CWReverse = cfgData.CWReverse;
                        i_CWL = (cfgData.CWLEnabled) ?
                            OffOnValues.on : OffOnValues.off;
                        i_Keyer = cfgData.Keyer;
                        i_KeyerSpeed = cfgData.KeyerSpeed;
                    }
                    catch (Exception ex)
                    {
                        Tracing.TraceLine("issue7620:read:exception:" + ex.Message, TraceLevel.Error);
                    }
                    finally
                    {
                        if (cfgStream != null) cfgStream.Dispose();
                    }
                }
            }
        }

        private string newGlobalProfile;
        /// <summary>
        /// Select the default profile if loaded.
        /// Before calling, call RaisePowerOff(), and PowerOn() when ready afterwards.
        /// </summary>
        /// <returns>true if selected and the info is loaded.</returns>
        /// <remarks>
        /// On an import, we'll wait for radio status of In_Use, then select the profile.
        /// </remarks>
        internal bool GetProfileInfo(bool postImport)
        {
            Tracing.TraceLine("getProfileInfo:" + postImport.ToString(), TraceLevel.Info);
            bool rv = true;

            // See if any default profiles.
            // Await to see if CurrentProfile is in the profile list.
            Tracing.TraceLine("getProfileInfo:awaiting default profile in GlobalProfileList", TraceLevel.Info);
            List<Profile_t> crnt = GetProfilesByType(ProfileTypes.global, GetDefaultProfiles());
            if ((crnt.Count > 0) && await(() =>
            {
                return (theRadio.ProfileGlobalList.Contains(crnt[0].Name));
            }, 3000))
            {
                // load the selected profile.
                Tracing.TraceLine("getProfileInfo:global profile present " + crnt[0].Name, TraceLevel.Info);
                // Select the current profile and wait til loaded.
                globalProfileDesired = crnt[0].Name;
                globalProfileLoaded = false;
                SelectProfile(crnt[0]);
                // Wait til loaded. (long wait)
                if (await(() =>
                {
                    return (globalProfileLoaded);
                }, 20000))
                {
                    Tracing.TraceLine("getProfileInfo:global profile loaded " + crnt[0].Name, TraceLevel.Info);
                }
            }
            else
            {
                if (crnt.Count > 0)
                {
                    // new profile, will get saved.
                    Tracing.TraceLine("GetProfileInfo:new profile" + crnt[0].Name, TraceLevel.Info);
                    newGlobalProfile = crnt[0].Name;
                }
            }

            // Load other profiles
            crnt = GetProfilesByType(ProfileTypes.tx, GetDefaultProfiles());
            if (crnt.Count > 0) SelectProfile(crnt[0]);

            crnt = GetProfilesByType(ProfileTypes.mic, GetDefaultProfiles());
            if (crnt.Count > 0) SelectProfile(crnt[0]);

            // Allocate any free slices.
            if (MyNumSlices < initialFreeSlices)
            {
                Tracing.TraceLine("GetProfileInfo:allocating free slices " + theRadio.PanadaptersRemaining, TraceLevel.Info);
                int oldRXVFO = RXVFO;
                int oldTXVFO = TXVFO;
                int oldNumSlices = MyNumSlices;
                while (MyNumSlices < initialFreeSlices)
                {
                    int n = MyNumSlices;
                    theRadio.RequestPanafall();
                    if (await(() =>
                    {
                        return (MyNumSlices > n);
                    }, 2000))
                    {
                        //Thread.Sleep(20); // wait a bit
                        //VFOToSlice(n).Mute = true;
                    }
                    else
                    {
                        // It might be there now.
                        if (MyNumSlices == n)
                        {
                            Tracing.TraceLine("GetProfileInfo:free slice not allocated", TraceLevel.Error);
                        }
                    }
                }

                _RXVFO = oldRXVFO;
                if (_RXVFO != noVFO) mySlices[_RXVFO].Active = true;
                _TXVFO = oldTXVFO;
                if (_TXVFO != noVFO) mySlices[_TXVFO].IsTransmitSlice = true;
            }

            _TotalNumSlices = theRadio.SliceList.Count;

            if (postImport)
            {
                Tracing.TraceLine("flex import operation complete:" + rv.ToString(), TraceLevel.Info);
                PCAudio = wasPCAudio;
                if (theRadio.ActiveSlice != null)
                {
                    FilterObj.RXFreqChange(theRadio.ActiveSlice);
                }
                raisePowerEvent(true);
                Directory.Delete(importDir, true);
                string msg = (rv) ? importedMsg : importFailMsg;
                MessageBox.Show(msg, statusHdr, MessageBoxButtons.OK);
            }
            return rv;
        }

        private bool setupFromScratch()
        {
            bool rv;
            // Radio was reset or never used with JJRadio before this.
            Tracing.TraceLine("setupFromScratch:panadapters:" + theRadio.PanadapterList.Count, TraceLevel.Info);
            // function to get pan adapters.
            //while (theRadio.PanadaptersRemaining > 0)
            while (MyNumSlices != initialFreeSlices)
            {
                int rem = theRadio.PanadaptersRemaining;
                theRadio.RequestPanafall();
                // wait for at least one new pan adapter
                if (!await(() =>
                {
                    return ((theRadio.PanadaptersRemaining < rem) |
                             (MyNumSlices == initialFreeSlices));
                }, 5000))
                {
                    Tracing.TraceLine("setupFromScratch:didn't get a pan adapter " + theRadio.PanadaptersRemaining, TraceLevel.Error);
                    break;
                }
                else
                {
                    Tracing.TraceLine("setupFromScratch:got a pan adapter " + theRadio.PanadaptersRemaining + ' ' + MyNumSlices, TraceLevel.Error);
                }
            }
            rv = (theRadio.PanadaptersRemaining == 0);
            if (rv)
            {
                Tracing.TraceLine("setupFromScratch:have pan and slices:" + MyNumPanadapters, TraceLevel.Info);
                // We have pan adapters and slices, so we're done.
                VFOToSlice(0).Active = true;
                VFOToSlice(0).Mute = false;
                Tracing.TraceLine("setupFromScratch:have 1 active slice:" + (MyNumSlices - 1), TraceLevel.Info);
                for (int i = 1; i < MyNumSlices; i++)
                {
                    mySlices[i].Mute = true;
                }

                VFOToSlice(RXVFO).TXAnt = theRadio.RXAntList[0];
                if (CanTransmit)
                {
                    _TXVFO = 0;
                    VFOToSlice(TXVFO).IsTransmitSlice = true;
                    VFOToSlice(TXVFO).TXAnt = theRadio.RXAntList[0];
                    theRadio.RFPower = 100;
                    theRadio.CWBreakIn = false;
                    theRadio.CWIambic = false;
                    theRadio.SpeechProcessorEnable = true;
                    theRadio.SimpleVOXEnable = false;

#if zero
                    CurrentProfile = preferredProfile;
                    createProfile();
#endif
                }

                Tracing.TraceLine("setupFromScratch:radio setup", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("setupFromScratch:didn't get pans and slices:" + MyNumPanadapters + ' ' + MyNumSlices, TraceLevel.Error);
            }
            _TotalNumSlices = theRadio.SliceList.Count;
            return rv;
        }

#if zero
        private bool createProfile()
        {
            bool rv = true;
            Tracing.TraceLine("createProfile:" + CurrentProfile, TraceLevel.Info);
            theRadio.CreateTXProfile(CurrentProfile);
            if (!await(() =>
            {
                return theRadio.ProfileTXList.Contains(CurrentProfile);
            }, 2000))
            {
                Tracing.TraceLine("CreateProfile:TX profile not created", TraceLevel.Error);
                rv = false;
            }
            return rv;
        }
#endif

        /// <summary>
        /// Flex Antenna tuner start/stop interrupt argument
        /// </summary>
        public class FlexAntTunerArg
        {
            public string Type;
            public string Status;
            public string SWR; // Good when stopped
            public FlexAntTunerArg(FlexTunerTypes type, ATUTuneStatus status, float swr)
            {
                Type = type.ToString();
                Status = status.ToString();
                SWR = swr.ToString("f1");
            }
            // Used to send a message
            public FlexAntTunerArg(string status)
            {
                Status = status;
                Type = null;
                SWR = null;
            }
        }
        public delegate void FlexAntTunerStartStopDel(FlexAntTunerArg arg);
        /// <summary>
        /// Antenna tuner start/stop event
        /// </summary>
        public event FlexAntTunerStartStopDel FlexAntTunerStartStop;
        internal void RaiseFlexAntTuneStartStop(FlexAntTunerArg arg)
        {
            if (FlexAntTunerStartStop != null)
            {
                Tracing.TraceLine("FlexAntTunerStartStop raised:" + arg.Type + ' ' + arg.Status + ' ' + arg.SWR, TraceLevel.Info);
                FlexAntTunerStartStop(arg);
            }
            else Tracing.TraceLine("FlexAntTunerStartStop not raised", TraceLevel.Verbose);
        }

        /// <summary>
        /// Argument for CapsChangeEvent
        /// </summary>
        public class CapsChangeArg
        {
            public RigCaps NewCaps;
            internal CapsChangeArg(RigCaps caps)
            {
                NewCaps = caps;
            }
        }
        public delegate void CapsChangeDel(CapsChangeArg arg);
        /// <summary>
        /// Raised when rig's capabilities change.
        /// </summary>
        public event CapsChangeDel CapsChangeEvent;
        private void raiseCapsChange(CapsChangeArg arg)
        {
            if (CapsChangeEvent != null)
            {
                Tracing.TraceLine("raiseCapsChange arg:" + +' ' + ((ulong)arg.NewCaps.setCaps).ToString("x"), TraceLevel.Error);
                CapsChangeEvent(arg);
            }
            else Tracing.TraceLine("raiseCapsChange not raised", TraceLevel.Error);
        }

        /// <summary>
        /// FlexControlKnob status
        /// </summary>
        /// <remarks>
        /// This is reported with the KnobOnOffEvent
        /// </remarks>
        public bool KnobStatus { get; set; }

        public delegate void KnobOnOffEventDel(object sender, bool OnOff);
        /// <summary>
        /// FlexKnob on/off event
        /// </summary>
        public event KnobOnOffEventDel KnobOnOffEvent;
        internal void raiseKnobOnnOff(bool onOff)
        {
            KnobStatus = onOff;
            if (KnobOnOffEvent != null)
            {
                KnobOnOffEvent(this, onOff);
            }
        }

        /// <summary>
        /// power status event handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="power">event argument</param>
        public delegate void PowerStatusHandler(object sender, bool power);
        /// <summary>
        /// power status event
        /// </summary>
        public event PowerStatusHandler PowerStatus;
        private void raisePowerEvent(bool on)
        {
            bool raise = (PowerStatus != null);
            Tracing.TraceLine("raisePowerEvent:" + on.ToString() + ' ' + raise.ToString(), TraceLevel.Info);
            if (raise)
            {
                PowerStatus(this, on);
            }
        }

        public delegate void TransmitChangeDel(object sender, bool value);
        /// <summary>
        /// Transmit status change event.
        /// </summary>
        public event TransmitChangeDel TransmitChange;
        private void raiseTransmitChange(bool status)
        {
            if (TransmitChange != null)
            {
                Tracing.TraceLine("raising TransmitChange:" + status.ToString(), TraceLevel.Info);
                TransmitChange(this, status);
            }
        }
        private string importDir;
        private bool wasPCAudio;
        internal void ImportProfile(string name)
        {
            // Save the import temp directory.
            importDir = name.Substring(0, name.LastIndexOf('\\'));
            raisePowerEvent(false);

            // If remote audio was on, turn it off.
            wasPCAudio = PCAudio;
            PCAudio = false; // started again in GetProfileInfo().

            // Do the import.
            theRadio.DatabaseImportComplete = false;
            theRadio.SendDBImportFile(name);
        }

        private CWX cwx;
        private void sendCWChar(char c)
        {
            if (theRadio == null) return;
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            cwx.Send(c.ToString());
#if CWMonitor
            if (useCWMon)
            {
                CWMon.Send(c);
            }
#endif
        }
        private void sendCWString(string str)
        {
            if ((theRadio == null) | string.IsNullOrEmpty(str)) return;
            // send only if in transmit mode or VOX is on
            if (!Transmit & (Vox == OffOnValues.off)) return;

            cwx.Send(str);
#if CWMonitor
            //sentChars.Append(str);
            if (useCWMon)
            {
                CWMon.Send(str);
            }
#endif
        }

        private void stopCW()
        {
            cwx.ClearBuffer();
#if CWMonitor
            if (useCWMon)
            {
                CWMonStop();
                CWMonStart();
            }
#endif
        }
#endregion

        // region - Memory stuff
#region memories
        /// <summary>
        /// current memory channel or -1.
        /// </summary>
        public int CurrentMemoryChannel
        {
            get
            {
                return ((NumberOfMemories > 0) && (memoryHandling != null)) ?
                  memoryHandling.CurrentMemoryChannel : -1;
            }
            set
            {
                if (memoryHandling != null) memoryHandling.CurrentMemoryChannel = value;
            }
        }

        /// <summary>
        /// Number of memories
        /// </summary>
        public int NumberOfMemories
        {
            get { return (memoryHandling == null) ? 0 : memoryHandling.NumberOfMemories; }
        }

        /// <summary>
        /// Select CurrentMemoryChannel's memory.
        /// </summary>
        /// <returns>true on success</returns>
        public bool SelectMemory()
        {
            Tracing.TraceLine("SelectMemory:" + CurrentMemoryChannel, TraceLevel.Info);
            if (memoryHandling != null)
            {
                return memoryHandling.SelectMemory();
            }
            else return false;
        }

        /// <summary>
        /// Select the named memory.
        /// </summary>
        /// <returns>true on success</returns>
        public bool SelectMemoryByName(string name)
        {
            Tracing.TraceLine("SelectMemoryByName:" + name, TraceLevel.Info);
            if (memoryHandling != null)
            {
                return memoryHandling.SelectMemoryByName(name);
            }
            else return false;
        }

        internal static string FullMemoryName(Memory m)
        {
            string name = (string.IsNullOrEmpty(m.Name)) ? m.Freq.ToString("F6") : m.Name;
            string group = (string.IsNullOrEmpty(m.Group)) ? "" : m.Group + '.';
            return group + name;
        }

        /// <summary>
        /// Get sorted list of full memory names.
        /// </summary>
        public List<string> MemoryNames()
        {
            List<string> rv;
            if (memoryHandling != null)
            {
                rv = memoryHandling.MemoryNames();
            }
            else
            {
                rv = new List<string>();
            }
            return rv;
        }

        /// <summary>
        /// Memory scan group
        /// </summary>
        public class ScanGroup
        {
            public string Name { get; set; }
            public List<string> Members;
            public bool Readonly; // false for a user-group
            public ScanGroup() { }
            public ScanGroup(string name, List<string> members, bool rdonly = false)
            {
                Name = name;
                Members = members;
                Readonly = rdonly;
            }
            public ScanGroup(ScanGroup group, FlexBase parent)
            {
                Name = group.Name;
                Readonly = false; // a user group.
                Members = new List<string>();
                // Add any group member that's still valid.
                foreach (Memory m in parent.theRadio.MemoryList)
                {
                    if (group.Members.Contains(m.Name))
                    {
                        Members.Add(m.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Get reserved scan groups, default is none.
        /// </summary>
        public List<ScanGroup> GetReservedGroups()
        {
            List<ScanGroup> rv = new List<ScanGroup>();
            if (memoryHandling == null) return rv;

            // Get list of all the rig's groups.
            List<string> myGroups = new List<string>();
            foreach(FlexMemories.MemoryElement el in memoryHandling.SortedMemories)
            {
                Memory m = el.Value;
                // if (!string.IsNullOrEmpty(m.Group) && !myGroups.Contains(m.Group))
                if (!myGroups.Contains(m.Group))
                {
                    myGroups.Add(m.Group);
                }
            }
            // Done if no memories.
            if (myGroups.Count == 0) return rv;

            // For each group, add the members.
            foreach(string group in myGroups)
            {
                List<string> memories = new List<string>();
                foreach(FlexMemories.MemoryElement el in memoryHandling.SortedMemories)
                {
                    Memory m = el.Value;
                    if (m.Group == group) memories.Add(FullMemoryName(m));
                }
                // Add the readOnly group.
                rv.Add(new ScanGroup(group, memories, true));
            }
            return rv;
        }
#endregion

        // Used for rig-specific functions.
        public delegate void updateDel();
        /// <summary>
        /// Allow the main program to access the radio's controls (see Flex6300Filter.cs)
        /// </summary>
        public class RigFields_t
        {
            /// <summary>
            /// RigFields form control
            /// </summary>
            public Control RigControl;
            /// <summary>
            /// RigFields update function.
            /// </summary>
            public updateDel RigUpdate;
            /// <summary>
            /// Memory info and display form.
            /// </summary>
            public Form Memories;
            /// <summary>
            /// Menu display form (unused)
            /// </summary>
            public Form Menus;
            /// <summary>
            /// Screen fields list.
            /// </summary>
            public Control[] ScreenFields;
            internal RigFields_t(Control c, updateDel rtn)
            {
                setup(c, rtn, null, null, null);
            }
            internal RigFields_t(Control c, updateDel rtn, Form f)
            {
                setup(c, rtn, f, null, null);
            }
            internal RigFields_t(Control c, updateDel rtn, Form mem, Form mnu)
            {
                setup(c, rtn, mem, mnu, null);
            }
            internal RigFields_t(Control c, updateDel rtn, Form mem, Form mnu,
                Control[] s)
            {
                setup(c, rtn, mem, mnu, s);
            }
            private void setup(Control c, updateDel rtn,
                Form mem, Form mnu, Control[] s)
            {
                RigControl = c;
                RigUpdate = rtn;
                Memories = mem;
                Menus = mnu;
                ScreenFields = s;
            }
            /// <summary>
            /// Close down the forms.
            /// </summary>
            internal void Close()
            {
                if (RigControl != null)
                {
                    RigControl.Dispose();
                    RigControl = null;
                }
                if (Memories != null)
                {
                    Memories.Dispose();
                    Memories = null;
                }
                if (Menus != null)
                {
                    Menus.Dispose();
                    Menus = null;
                }
            }
        }
        /// <summary>
        /// Gets the rig-specific fields
        /// </summary>
        public RigFields_t RigFields
        {
            get;
            internal set;
        }

        private FlexMemories memoryHandling
        {
            get { return ((RigFields != null) && (RigFields.Memories != null)) ? (FlexMemories)RigFields.Memories : null; }
        }

        /// <summary>
        /// Tone frequencies
        /// </summary>
        public float[] ToneFrequencyTable;

        // Valid tone/CTSS frequencies
        private static float[] myToneFrequencyTable =
        {
            67.0F, 69.3F, 71.9F, 74.4F, 77.0F, 79.7F, 82.5F, 85.4F, 88.5F, 91.5F,
            94.8F, 97.4F, 100.0F, 103.5F, 107.2F, 110.9F, 114.8F, 118.8F, 123.0F,
            127.3F, 131.8F, 136.5F, 141.3F, 146.2F, 151.4F, 156.7F, 162.2F, 167.9F,
            173.8F, 179.9F, 186.2F, 192.8F, 203.5F, 206.5F, 210.7F, 218.1F, 225.7F,
            229.1F, 233.6F, 241.8F, 250.3F, 254.1F, 1750F
        };
    }
}
