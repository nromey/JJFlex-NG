// ****************************************************************************
///*!	\file Discovery.cs
// *	\brief Facilitates reception of Discovery packets
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2012-03-05
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.Vita;
using Flex.Util;

namespace Flex.Smoothlake.FlexLib
{
    public delegate void RadioDiscoveredEventHandler(Radio radio);

    class Discovery
    {
        private const int DISCOVERY_PORT = 4992;
        private static UdpClient udp;

        private static CancellationTokenSource _loopCts;

        public static void Start()
        {
            // JJFlex diagnostic round 2 (Don 4.2.18 silent-discovery, 2026-04-30):
            // unconditional build-marker so we can confirm this binary is running
            // even when zero packets arrive (round 1 left this ambiguous).
            Trace.WriteLine("Discovery.Start: JJFlex 4218-discovery-diagnostic build R6 active (chain+MMCSS-bypass)");

            // Enumerate network interfaces so we can see what JJFlex sees on the
            // host. Suspect 2 is interface-binding / .NET-stack regression — this
            // tells us which adapters are visible and which IPv4 addresses they hold.
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var ipProps = nic.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        Trace.WriteLine($"Discovery.Start: nic '{nic.Name}' type={nic.NetworkInterfaceType} ipv4={addr.Address} mask={addr.IPv4Mask}");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.Start: nic enumeration threw {ex.GetType().Name}: {ex.Message}");
            }

            bool done = false;
            int error_count = 0;
            while (!done)
            {
                try
                {
                    udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                    Trace.WriteLine($"Discovery.Start: bound UdpClient to {udp.Client.LocalEndPoint}");
                    done = true;
                }
                catch (SocketException ex)
                {
                    Trace.WriteLine($"Discovery.Start: bind attempt failed ({ex.SocketErrorCode}), retry {error_count}");
                    // do this up to 60 times (60 sec)
                    if (error_count++ > 60) // after 60, give up and rethrow the exception
                        throw new SocketException(ex.ErrorCode);
                    else Thread.Sleep(1000);
                }
            }
            _loopCts = new CancellationTokenSource();

            // R3: Self-test the discovery socket BEFORE the main Receive loop starts.
            // Sends three probes (loopback, NIC-self per active adapter, limited
            // broadcast) and confirms each one arrives at the bound socket. The
            // resulting trace lines disambiguate "socket is healthy / problem is
            // upstream" from "socket itself can't receive certain traffic classes".
            // See round-3 NOTES for the decisive matrix this produces.
            SelfTest();

            // R4: After SelfTest, run a 5-second synchronous receive drain on the
            // SAME bound socket BEFORE handing off to the async Receive loop. This
            // tests whether the radio's broadcast packets are arriving at the socket
            // at all -- they would land in the kernel UDP buffer regardless of which
            // read mechanism we use, and SYNC reads have a different code path than
            // the NET6+ ReceiveAsync(token) machinery used by the main loop.
            //
            // If sync drain receives radio packets but async loop sees none -> bug
            // is in token-aware ReceiveAsync (we revert to no-token variant or
            // sync-thread pattern). If sync drain also sees nothing -> the issue
            // is upstream of the socket and we need to look elsewhere in FlexLib.
            SyncReceiveDrain(durationMs: 5000);

            Task.Run(Receive);
        }

        private static void SyncReceiveDrain(int durationMs)
        {
            try
            {
                Trace.WriteLine($"Discovery.SyncDrain: starting {durationMs}ms synchronous receive test on bound socket");

                int savedTimeout = udp.Client.ReceiveTimeout;
                udp.Client.ReceiveTimeout = 500;

                var deadline = DateTime.UtcNow.AddMilliseconds(durationMs);
                int packetCount = 0;
                int otherSourceCount = 0;
                int radioSourceCount = 0;

                try
                {
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                            var buf = new byte[2048];
                            int len = udp.Client.ReceiveFrom(buf, ref ep);
                            packetCount++;

                            string srcAddr = (ep as IPEndPoint)?.Address.ToString() ?? "?";
                            bool isLocal = srcAddr == "127.0.0.1" ||
                                           srcAddr.StartsWith("192.168.1.21") ||
                                           srcAddr == (udp.Client.LocalEndPoint as IPEndPoint)?.Address.ToString();

                            if (isLocal) otherSourceCount++; else radioSourceCount++;

                            // Try to parse the VITA preamble for diagnostic detail
                            string vitaInfo = "";
                            if (len >= 16)
                            {
                                try
                                {
                                    var v = new VitaPacketPreamble(buf);
                                    vitaInfo = $" OUI=0x{v.class_id.OUI:X} pktType={v.header.pkt_type} classCode=0x{v.class_id.PacketClassCode:X}";
                                }
                                catch (Exception px) { vitaInfo = $" (preamble parse threw {px.GetType().Name})"; }
                            }

                            Trace.WriteLine($"Discovery.SyncDrain: rx pkt #{packetCount} len={len} from={ep}{vitaInfo}");
                        }
                        catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
                        {
                            // expected when no packets arrive within 500ms; loop back to deadline check
                        }
                    }
                }
                finally
                {
                    udp.Client.ReceiveTimeout = savedTimeout;
                }

                Trace.WriteLine($"Discovery.SyncDrain: complete -- received {packetCount} total ({radioSourceCount} from non-local sources, {otherSourceCount} from local sources)");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.SyncDrain: outer threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SelfTest()
        {
            try
            {
                Trace.WriteLine("Discovery.SelfTest: starting socket-validation probes (R3)");

                var marker = System.Text.Encoding.ASCII.GetBytes("JJFX-SELFTEST-R3");
                var probeTargets = BuildSelfTestProbeTargets();

                // Save and override the receive timeout so we can synchronously
                // wait for each probe without blocking forever. Restored at end.
                int savedTimeout = udp.Client.ReceiveTimeout;
                udp.Client.ReceiveTimeout = 500;

                try
                {
                    foreach (var probe in probeTargets)
                    {
                        SendOneSelfTestProbe(probe.label, probe.target, marker);
                        TryReceiveOneSelfTestProbe(probe.label, marker);
                    }
                }
                finally
                {
                    udp.Client.ReceiveTimeout = savedTimeout;
                }

                Trace.WriteLine("Discovery.SelfTest: complete");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.SelfTest: outer threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static List<(string label, IPEndPoint target)> BuildSelfTestProbeTargets()
        {
            var list = new List<(string, IPEndPoint)>
            {
                ("loopback", new IPEndPoint(IPAddress.Loopback, DISCOVERY_PORT))
            };

            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        list.Add(($"nic-self-{nic.Name}", new IPEndPoint(addr.Address, DISCOVERY_PORT)));
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.SelfTest: NIC probe enumeration threw {ex.GetType().Name}: {ex.Message}");
            }

            list.Add(("limited-broadcast", new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT)));
            return list;
        }

        private static void SendOneSelfTestProbe(string label, IPEndPoint target, byte[] marker)
        {
            try
            {
                using var sender = new UdpClient();
                sender.EnableBroadcast = true;
                sender.Send(marker, marker.Length, target);
                Trace.WriteLine($"Discovery.SelfTest: SENT probe '{label}' -> {target}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.SelfTest: probe '{label}' SEND failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryReceiveOneSelfTestProbe(string label, byte[] marker)
        {
            try
            {
                EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                var buf = new byte[1024];
                int len = udp.Client.ReceiveFrom(buf, ref ep);

                bool isOurs = len >= marker.Length;
                if (isOurs)
                {
                    for (int i = 0; i < marker.Length; i++)
                    {
                        if (buf[i] != marker[i]) { isOurs = false; break; }
                    }
                }

                if (isOurs)
                {
                    Trace.WriteLine($"Discovery.SelfTest: RECEIVED probe '{label}' (len={len} from={ep})");
                }
                else
                {
                    Trace.WriteLine($"Discovery.SelfTest: probe '{label}' got OTHER packet (len={len} from={ep}) -- this is fine, not our marker");
                }
            }
            catch (SocketException sex) when (sex.SocketErrorCode == SocketError.TimedOut)
            {
                Trace.WriteLine($"Discovery.SelfTest: probe '{label}' NOT RECEIVED within 500ms (TimedOut)");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Discovery.SelfTest: probe '{label}' RECEIVE threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Stop()
        {
            _loopCts.Cancel();
            udp?.Close();
        }

        private static async void Receive()
        {
            // JJFlex patch: FlexLib Discovery had a race where a second Discovery.Start()
            // (e.g. via apiInit force=true path) could spawn a new Receive task while the
            // previous one was still pending; on exit the old task would null the static
            // `udp` out from under the new task, causing an NRE at the await below.
            // Fix: capture a local reference, guard against null, and only null the static
            // if we still own it. See flexlib-discovery-nre-report.txt and MIGRATION.md.
            //Stopwatch watch = new Stopwatch();
            var token = _loopCts.Token;
            var localUdp = udp;
            Debug.WriteLine($"Discovery.Receive: task started (udp={(localUdp == null ? "null" : "set")})");
            if (localUdp == null)
            {
                Debug.WriteLine("Discovery.Receive: exiting immediately, udp was null at entry");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
#if NET6_0_OR_GREATER
                    var packet = await localUdp.ReceiveAsync(token);
#else
                    var packet = await localUdp.ReceiveAsync();
#endif
                    //watch.Restart();

                    // since the call above is blocking, we need to check active again here
                    if (token.IsCancellationRequested)
                        break;

                    // JJFlex diagnostic (Don 4.2.18 silent-discovery investigation 2026-04-30):
                    // log every UDP packet that arrives at the discovery socket BEFORE any
                    // length/VITA filtering so we can disambiguate "no packets reach socket"
                    // from "packets arrive but are filtered out". Remove once the bug is
                    // identified — this is debug-build instrumentation only.
                    Trace.WriteLine($"Discovery: rx pkt len={packet.Buffer.Length} from={packet.RemoteEndPoint}");

                    // ensure that the packet is at least long enough to inspect for VITA info
                    if (packet.Buffer.Length < 16)
                    {
                        Trace.WriteLine("Discovery: rx pkt rejected (length < 16)");
                        continue;
                    }

                    var vita = new VitaPacketPreamble(packet.Buffer);
                    Trace.WriteLine($"Discovery: rx VITA OUI=0x{vita.class_id.OUI:X} pktType={vita.header.pkt_type} classCode=0x{vita.class_id.PacketClassCode:X}");

                    // Check for a valid discovery packet
                    if (vita.class_id.OUI != VitaFlex.FLEX_OUI ||
                        vita.header.pkt_type != VitaPacketType.ExtDataWithStream ||
                        vita.class_id.PacketClassCode != VitaFlex.SL_VITA_DISCOVERY_CLASS)
                    {
                        Trace.WriteLine($"Discovery: rx VITA filtered (expected OUI=0x{VitaFlex.FLEX_OUI:X} ExtDataWithStream classCode=0x{VitaFlex.SL_VITA_DISCOVERY_CLASS:X})");
                        continue;
                    }

                    Trace.WriteLine($"Discovery: rx VITA accepted, processing discovery payload");

                    Radio radio = ProcessVitaDiscoveryDataPacket(
                        new VitaDiscoveryPacket(packet.Buffer, packet.Buffer.Length));
                    OnRadioDiscoveredEventHandler(radio);

                    //watch.Stop();
                    //if(radio.Serial == "3424-1213-8601-4043")
                    //    Debug.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff")+": Discovery watch stop (" + watch.ElapsedMilliseconds + " ms)");
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                    Debug.WriteLine("Discovery.Receive: socket disposed under us, exiting");
                    break;
                }
                catch (SocketException ex) when (
                    ex.SocketErrorCode is SocketError.OperationAborted)
                {
                    break;
                }
                catch (SocketException sex)
                {
                    Debug.WriteLine($"Discovery.Receive: socket exception {sex.SocketErrorCode}, exiting");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Discovery::Receive: Exception reading from UDP socket: {ex}");
                    break;
                }
            }

            try { localUdp.Close(); } catch { }
            if (udp == localUdp) udp = null;    // only null the static if it's still ours
            Debug.WriteLine("Discovery.Receive: task exited cleanly");
        }

        private static Radio ProcessVitaDiscoveryDataPacket(VitaDiscoveryPacket packet)
        {
            Radio radio = new Radio();
            string guiClientProgramsCsv = null;
            string guiClientHandlesCsv = null;

            string[] words = packet.payload.Trim().Split(' ');

            foreach (string kv in words)
            {
                string[] tokens = kv.Split('=');
                if (tokens.Length != 2)
                {
                    //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0].Trim();
                string value = tokens[1].Trim();
                value = value.Replace("\0", "");

                switch (key.ToLower())
                {
                    case "available_clients":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.AvailableClients = temp;
                        }
                        break;
                    case "available_panadapters":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.AvailablePanadapters = temp;
                        }
                        break;
                    case "available_slices":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.AvailableSlices = temp;
                        }
                        break;
                    case "callsign":
                        radio.Callsign = value;
                        break;
                    case "discovery_protocol_version":
                        {
                            ulong temp;
                            bool b = FlexVersion.TryParse(value, out temp);
                            if (!b)
                            {
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Error converting version string (" + value + ")");
                                continue;
                            }

                            radio.DiscoveryProtocolVersion = temp;
                        }
                        break;
                    case "fpc_mac":
                        radio.FrontPanelMacAddress = value.Replace('-', ':').Trim();
                        break;
                    case "gui_client_ips":
                        radio.GuiClientIPs = value;
                        break;
                    case "gui_client_hosts":
                        radio.GuiClientHosts = value;
                        break;
                    case "gui_client_programs":
                        guiClientProgramsCsv = value;
                        break;
                    case "gui_client_stations":
                        radio.GuiClientStations = value.Replace('\u007f', ' ');
                        break;
                    case "gui_client_handles":
                        guiClientHandlesCsv = value;
                        break;
                    case "inuse_host":
                        radio.InUseHost = value;
                        break;
                    case "inuse_ip":
                        radio.InUseIP = value;
                        break;
                    case "ip":
                        {
                            IPAddress temp;
                            bool b = IPAddress.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.IP = temp;
                        }
                        break;
                    case "is_system_model":
                        {
                            if (!uint.TryParse(value, out uint temp) || temp > 1)
                            {
                                Trace.WriteLine($"FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair ({kv})");
                                continue;
                            }

                            radio.IsSystemModel = Convert.ToBoolean(temp);
                        }
                        break;
                    case "license_is_unknown":
                        {
                            if (!uint.TryParse(value, out uint temp) || temp > 1)
                            {
                                Debug.WriteLine($"FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair ({kv})");
                                continue;
                            }

                            radio.HasUnknownRadioLicense = Convert.ToBoolean(temp);
                        }
                        break;
                    case "licensed_clients":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.LicensedClients = temp;
                        }
                        break;
                    case "max_licensed_version":
                        radio.MaxLicensedVersion = StringHelper.Sanitize(value);
                        break;
                    case "max_panadapters":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.MaxPanadapters = temp;
                        }
                        break;
                    case "max_slices":
                        {
                            int temp;
                            bool b = int.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.MaxSlices = temp;
                        }
                        break;
                    case "model":
                        radio.Model = value;
                        break;
                    case "nickname":
                        radio.Nickname = value;
                        break;
                    case "port":
                        {
                            ushort temp;
                            bool b = ushort.TryParse(value, out temp);
                            if (!b)
                            {
                                //Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.CommandPort = temp;
                        }
                        break;
                    case "radio_license_id":
                        radio.RadioLicenseId = StringHelper.Sanitize(value);
                        break;
                    case "serial":
                        radio.Serial = StringHelper.Sanitize(value);
                        break;
                    case "status":
                        radio.Status = value;
                        break;
                    case "version":
                        {
                            ulong temp;
                            bool b = FlexVersion.TryParse(value, out temp);
                            if (!b)
                            {
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Error converting version string (" + value + ")");
                                continue;
                            }

                            radio.Version = temp;
                        }
                        break;                    
                    case "wan_connected":
                        {
                            uint temp;
                            bool b = uint.TryParse(value, out temp);
                            if (!b || temp > 1)
                            {
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid value (" + kv + ")");
                                continue;
                            }

                            radio.IsInternetConnected = Convert.ToBoolean(temp);
                        }
                        break;
                    case "external_port_link":
                    {
                        if (uint.TryParse(value, out var link))
                        {
                            radio.ExternalPortLink = link == 1;
                        }

                        break;
                    }
                    case "turf_region":
                        radio.TurfRegion = value;
                        break;
                }
            }

            List<GUIClient> guiClients = ParseGuiClientsFromDiscovery(guiClientProgramsCsv, radio.GuiClientStations, guiClientHandlesCsv);
            lock (radio.GuiClientsLockObj)
            {
                radio.GuiClients = guiClients;
            }

            return radio;
        }

        public static List<GUIClient> ParseGuiClientsFromDiscovery(string guiClientProgramsCsv, string guiClientStationCsv, string guiClientHandlesCsv)
        {
            if (string.IsNullOrEmpty(guiClientProgramsCsv) || string.IsNullOrEmpty(guiClientStationCsv) || string.IsNullOrEmpty(guiClientHandlesCsv))
            {
                return new List<GUIClient>();
            }

            var programs = guiClientProgramsCsv.Split(',');
            var stations = guiClientStationCsv.Split(',');
            var handles = guiClientHandlesCsv.Split(',');

            if (programs.Length != stations.Length ||
                programs.Length != handles.Length ||
                stations.Length != handles.Length)
            {
                // The lengths of these lists must match.
                return new List<GUIClient>();
            }

            List<GUIClient> guiClients = new List<GUIClient>();
            for (int i = 0; i < programs.Length; i++)
            {
                uint handle_uint;
                StringHelper.TryParseInteger(handles[i], out handle_uint);

                string station = stations[i].Replace('\u007f', ' ');
                GUIClient newGuiClient = new GUIClient(handle: handle_uint, client_id: null, program: programs[i], station: station, is_local_ptt: false);
                guiClients.Add(newGuiClient);
            }

            return guiClients;
        }

        public static event RadioDiscoveredEventHandler RadioDiscovered;

        public static void OnRadioDiscoveredEventHandler(Radio radio)
        {
            if (RadioDiscovered == null) return;
            RadioDiscovered(radio);
        }
    }
}