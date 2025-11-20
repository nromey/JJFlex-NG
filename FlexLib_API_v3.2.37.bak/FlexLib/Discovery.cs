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
using System.Linq;
using System.Text;

using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Flex.Util;
using Flex.Smoothlake.Vita;


namespace Flex.Smoothlake.FlexLib
{
    public delegate void RadioDiscoveredEventHandler(Radio radio);

    class Discovery
    {
        private const int DISCOVERY_PORT = 4992;
        private static UdpClient udp;
        private static bool active = false;        

        //struct DiscoveryObject
        //{
        //    public uint ip;
        //    public ushort port;
        //    public ushort radios; // not used
        //    public uint mask; // not used
        //    public uint model_len;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string model;
        //    public uint serial_len;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string serial;
        //    public uint name_len;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string name;
        //    public uint version_len;
        //    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        //    public string version;
        //}

        public static void Start()
        {
            active = true;

            bool done = false;
            int error_count = 0;
            while (!done)
            {
                try
                {
                    udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
                    done = true;
                }
                catch (SocketException ex)
                {
                    // do this up to 60 times (60 sec)
                    if (error_count++ > 60) // after 60, give up and rethrow the exception
                        throw new SocketException(ex.ErrorCode);
                    else Thread.Sleep(1000);
                }
            }

            Thread t = new Thread(new ThreadStart(Receive));
            t.Name = "FlexAPI Discovery Thread";
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        public static void Stop()
        {
            active = false;
            //udp.Close();
            //udp = null;
        }

        private static void Receive()
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

            while (active)
            {
                byte[] data = udp.Receive(ref ep);
                //Debug.WriteLine("UDP Received: " + buf.Length);

                // since the call above is blocking, we need to check active again here
                if (!active) break;

                //bool vita_discovery = false;

                // ensure that the packet is at least long enough to inspect for VITA info
                if (data.Length >= 16)
                {
                    VitaPacketPreamble vita = new VitaPacketPreamble(data);

                    // ensure the packet has our OUI in it -- looks like it came from us
                    if (vita.class_id.OUI == VitaFlex.FLEX_OUI)
                    {
                        // handle discovery packets here
                        switch (vita.header.pkt_type)
                        {
                            case VitaPacketType.ExtDataWithStream:
                                switch (vita.class_id.PacketClassCode)
                                {
                                    case VitaFlex.SL_VITA_DISCOVERY_CLASS:
                                        ProcessVitaDiscoveryDataPacket(new VitaDiscoveryPacket(data, data.Length));
                                        //vita_discovery = true;
                                        break;
                                }
                                break;
                        }
                    }
                }

                /* No longer supporting older discovery protocol
                 * 
                // skip any further processing if it was a vita discovery packet
                if (vita_discovery) continue;

                DiscoveryObject obj;
                if (data.Length != Marshal.SizeOf(typeof(DiscoveryObject))) continue;

                try
                {
                    //GCHandle pinnedPacket = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    //obj = (DiscoveryObject)Marshal.PtrToStructure(
                    //    pinnedPacket.AddrOfPinnedObject(),
                    //    typeof(DiscoveryObject));
                    //pinnedPacket.Free();

                    obj.ip = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 0));
                    obj.port = ByteOrder.SwapBytes(BitConverter.ToUInt16(data, 4));
                    obj.radios = ByteOrder.SwapBytes(BitConverter.ToUInt16(data, 6));
                    obj.mask = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 8));
                    obj.model_len = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 12));
                    obj.model = Encoding.UTF8.GetString(data, 16, 32).Trim('\0');
                    obj.serial_len = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 48));
                    obj.serial = Encoding.UTF8.GetString(data, 52, 32).Trim('\0');
                    obj.name_len = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 84));
                    obj.name = Encoding.UTF8.GetString(data, 88, 32).Trim('\0');
                    obj.version_len = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, 120));
                    obj.version = Encoding.UTF8.GetString(data, 124, 32).Trim('\0');

                    //Debug.WriteLine(DateTime.Now.ToLongTimeString() + " Model:" + obj.model + " ip:" + new IPAddress(obj.ip).ToString());
                    OnRadioDiscoveredEventHandler(new Radio(obj.model, obj.serial, "", new IPAddress(obj.ip), obj.version));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("EXCEPTION: " + ex.ToString());
                }
                 * 
                 */
            }

            udp.Close();
            udp = null;
        }

        private static void ProcessVitaDiscoveryDataPacket(VitaDiscoveryPacket packet)
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
                    Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0].Trim();
                string value = tokens[1].Trim();
                value = value.Replace("\0", "");

                switch (key.ToLower())
                {
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
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.IP = temp;
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
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid key/value pair (" + kv + ")");
                                continue;
                            }

                            radio.CommandPort = temp;
                        }
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
                    case "max_licensed_version":
                        radio.MaxLicensedVersion = StringHelper.Sanitize(value);
                        break;
                    case "radio_license_id":
                        radio.RadioLicenseId = StringHelper.Sanitize(value);
                        break;
                    case "requires_additional_license":
                        {
                            uint temp;
                            bool b = uint.TryParse(value, out temp);
                            if (!b || temp > 1)
                            {
                                Debug.WriteLine("FlexLib::Discovery::ProcessVitaDiscoveryDataPacket: Invalid value (" + kv + ")");
                                continue;
                            }

                            radio.RequiresAdditionalLicense = Convert.ToBoolean(temp);
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
                }
            }

            List<GUIClient> guiClients = ParseGuiClientsFromDiscovery(guiClientProgramsCsv, radio.GuiClientStations, guiClientHandlesCsv);
            lock (radio.GuiClientsLockObj)
            {
                radio.GuiClients = guiClients;
            }

            OnRadioDiscoveredEventHandler(radio);
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