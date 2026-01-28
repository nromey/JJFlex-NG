// ****************************************************************************
///*!	\file OpusStream.cs
// *	\brief Represents a single Audio Stream (narrow, mono)
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2013-11-18
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Flex.UiWpfFramework.Mvvm;
using Flex.Util;
using Flex.Smoothlake.Vita;
using System.Threading;

namespace Flex.Smoothlake.FlexLib
{
    public class OpusStream : ObservableObject
    {
        private Radio _radio;
        private bool _closing = false;


        public Object OpusRXListLockObj = new Object();
        public double LastOpusTimestampConsumed = 0;
        public SortedList<double, VitaOpusDataPacket> _opusRXList = new SortedList<double, VitaOpusDataPacket>();
        public OpusStream(Radio radio)
        {
            this._radio = radio;
            _opusRXList.Capacity = 50;
        }

        public void OpusStartUpdateRXRateThread()
        {
            Thread t = new Thread(new ThreadStart(UpdateRates));
            t.Name = "OpusStream UpdateRXRate Thread";
            t.IsBackground = true;
            t.Priority = ThreadPriority.Normal;
            t.Start();
        }

        private uint _rxStreamId;
        public uint RXStreamID
        {
            get { return _rxStreamId; }
            internal set { _rxStreamId = value; }
        }

        private uint _txStreamID;
        public uint TXStreamID
        {
            get { return _txStreamID; }
        }

        private bool _opusRXStreamStopped;
        public bool OpusRXStreamStopped
        {
            get { return _opusRXStreamStopped; }
        }


        private bool _radioAck = false;
        public bool RadioAck
        {
            get { return _radioAck; }
            internal set
            {
                if (_radioAck != value)
                {
                    _radioAck = value;
                    RaisePropertyChanged("RadioAck");
                }
            }
        }

        private bool _remoteRxOn = false;
        public bool RemoteRxOn
        {
            get { return _remoteRxOn; }
            set
            {
                if (_remoteRxOn != value)
                {
                    _remoteRxOn = value;
                    _radio.SendCommand("remote_audio rx_on " + Convert.ToByte(_remoteRxOn));
                    RaisePropertyChanged("RemoteRxOn");
                }
            }
        }

        private double _byteSumRX = 0;
        private double _bytesPerSecFromRadio;
        public double BytesPerSecFromRadio
        {
            get { return _bytesPerSecFromRadio; }
            set
            {
                if (_bytesPerSecFromRadio != value)
                {
                    _bytesPerSecFromRadio = value;
                    RaisePropertyChanged("BytesPerSecFromRadio");
                }
            }
        }

        private double _byteSumTX = 0;
        private double _bytesPerSecToRadio;
        public double BytesPerSecToRadio
        {
            get { return _bytesPerSecToRadio; }
            set
            {
                if (_bytesPerSecToRadio != value)
                {
                    _bytesPerSecToRadio = value;
                    RaisePropertyChanged("BytesPerSecToRadio");
                }
            }
        }

        private IPAddress _ip;
        public IPAddress IP
        {
            get { return _ip; }
        }

        private int _port;
        public int Port
        {
            get { return _port; }
        }

        public bool RequestOpusStreamFromRadio()
        {
            // check to see if this object has already been activated
            if (_radioAck) return false;

            // check to ensure this object is tied to a radio object
            if (_radio == null) return false;

            // check to make sure the radio is connected
            if (!_radio.Connected) return false;

            // send the command to the radio to create the object...need to change this..
            //_radio.SendReplyCommand(new ReplyHandler(UpdateStreamID), "stream create opus");

            return true;
        }

        private void UpdateStreamID(uint resp_val, string s)
        {
            if (resp_val != 0) return;

            bool b = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _rxStreamId);

            if (!b)
            {
                Debug.WriteLine("OpusStream::UpdateStreamID-Error parsing Stream ID (" + s + ")");
                return;
            }

            _mine = true;

            _radio.AddOpusStream(this);
        }

        private bool _mine = false;
        public bool Mine
        {
            get { return _mine; }
        }

        public void Close()
        {
            Debug.WriteLine("OpusStream::Close (0x" + _rxStreamId.ToString("X") + ")");
            _radio.RemoveOpusStream();
        }

        internal void Remove()
        {
            _closing = true;
            Debug.WriteLine("OpusStream::Remove (0x" + _rxStreamId.ToString("X") + ")");
            _radio.SendCommand("stream remove 0x" + _rxStreamId.ToString("X"));
        }

        private int _errorCount = 0;
        public int ErrorCount
        {
            get { return _errorCount; }
            set
            {
                if (_errorCount != value)
                {
                    _errorCount = value;
                    RaisePropertyChanged("ErrorCount");
                }
            }
        }

        private int _opusPacketTotalCount = 0;
        public int OpusPacketTotalCount
        {
            get { return _opusPacketTotalCount; }
            set
            {
                if (_opusPacketTotalCount != value)
                {
                    _opusPacketTotalCount = value;
                    // only raise the property change every 100 packets (performance)
                    if (_opusPacketTotalCount % 100 == 0) RaisePropertyChanged("OpusPacketTotalCount");
                }
            }
        }

        private int error_count_out_of_order = 0;
        // note that the "Lost" indicator assumes we only lost a single packet -- this is probably reasonable for "decent" networks
        private void PrintStats()
        {
            int lost = _errorCount - error_count_out_of_order;
            Debug.WriteLine("Opus Stream 0x" + _rxStreamId.ToString("X").PadLeft(8, '0') +
                "-Reversed: " + error_count_out_of_order + " (" + (error_count_out_of_order * 100.0 / _opusPacketTotalCount).ToString("f2") + ")" +
                "  Lost: " + lost + " (" + (lost * 100.0 / _opusPacketTotalCount).ToString("f2") + ")" +
                "  Total: " + _opusPacketTotalCount);
        }


        int max_num = 0;
        bool first_time = true;
        int starting_index = 0;

        private void PatternChecker(byte[] data)
        {
            // 0 
            // 0 1
            // 0 1 2
            // 0 1 2 3 
            // ....
            // 0 1 2 .... 254 255
            // 0
            // 0 1

            // find a 0 first
            if (first_time)
            {
                for (int i = 1; i < data.Length; i++)
                {
                    if (data[i] == 0)
                    {
                        max_num = data[i - 1];
                        if (max_num == 255)
                        {
                            max_num = -1;
                        }
                        starting_index = i;
                        first_time = false;
                        i = data.Length;
                        Debug.WriteLine("FIRST TIME: max_num = " + max_num);
                        //break;
                    }
                }
            }

            for (int i = starting_index + 1; i < data.Length; i++)
            {
                if (data[i] != data[i - 1] + 1)
                {
                    if (data[i] == 0 && data[i - 1] == max_num + 1)
                    {
                        max_num = data[i - 1];
                        //Debug.WriteLine("max_num = " + max_num + " i= "+i);
                    }
                    else
                    {
                        Debug.WriteLine("Pattern fail at index " + i + ".  Expected max_num=" + (max_num + 1) + " got " + data[i - 1]);
                        Debug.WriteLine("data[" + (i - 1) + "]= " + data[i - 1] + " data[" + i + "]= " + data[i] + " data[" + (i + 1) + "]= " + data[i + 1]);
                        first_time = true;
                        break;
                    }

                    if (max_num == 255)
                        max_num = -1;
                }
            }

            starting_index = 0;
        }

        private const int NOT_INITIALIZED = 99;
        private int last_packet_count = NOT_INITIALIZED;

        internal void AddRXData(VitaOpusDataPacket packet)
        {
            OpusPacketTotalCount++;
#if DEBUG_STATS
            if (_opusPacketTotalCount % 1000 == 0) PrintStats();
#endif
            //Debug.WriteLine("OpusTimestamp: " + packet.timestamp_int + "." + packet.timestamp_frac);

            double timestamp_key = packet.timestamp_int + (packet.timestamp_frac / Math.Pow(2,16));

            //Debug.WriteLine("OpusTimestampKey: " + timestamp_key);

            lock (this)
            {
                _byteSumRX += packet.Length;
            }

            int packet_count = packet.header.packet_count;

            // Only queue if the packet is more recent than the last one the 
            // Audio callback consumed

            if (LastOpusTimestampConsumed < timestamp_key)
            {
                lock (OpusRXListLockObj)
                {
                    if ( _opusRXList.Count > 30 )
                    {
                        Debug.Write("X");
                        _opusRXList.Clear(); /* Overflow event */
                    }

                    _opusRXList.Add(timestamp_key, packet);
                }
            }
            else
            {
                // Old data we no longer care about
                Debug.Write("o");
            }
            //normal case -- this is the next packet we are looking for, or it is the first one
            if (packet_count == (last_packet_count + 1) % 16 || last_packet_count == NOT_INITIALIZED)
            {
                last_packet_count = packet_count;
            }
            else
            {
                Debug.WriteLine("Opus Audio: Expected " + ((last_packet_count + 1) % 16) + "  got " + packet_count);
                ErrorCount++;

                last_packet_count = packet_count;
            }
        }

        private void UpdateRates()
        {
            while (!_closing)
            {
                lock (this)
                {
                    _bytesPerSecFromRadio = _byteSumRX;
                    _bytesPerSecToRadio = _byteSumTX;
                    _byteSumRX = 0;
                    _byteSumTX = 0;
                }
                RaisePropertyChanged("BytesPerSecFromRadio");
                RaisePropertyChanged("BytesPerSecToRadio");
                //Debug.WriteLine("OpusStream- Rate: " + (_bytesPerSecFromRadio * 8.0 / 1000.0 / 1000.0).ToString("f3")+ " Mbps");
                Thread.Sleep(1000);
            }
        }

        //private VitaIFDataPacket _txPacket2;
        private VitaOpusDataPacket _txPacket;
        public void AddTXData(byte[] tx_data)
        {
            lock(this)
                _byteSumTX += tx_data.Length;

            if (_txPacket == null)
            {
                _txPacket = new VitaOpusDataPacket();
                _txPacket.header.pkt_type = VitaPacketType.ExtDataWithStream;
                _txPacket.header.c = true;
                _txPacket.header.t = false;
                _txPacket.header.tsi = VitaTimeStampIntegerType.Other;
                _txPacket.header.tsf = VitaTimeStampFractionalType.SampleCount;

                //_txPacket.stream_id = _txStreamID;
                _txPacket.stream_id = 0x4B000000;   // TODO: don't hardcode this
                _txPacket.class_id.OUI = 0x001C2D;
                _txPacket.class_id.InformationClassCode = 0x534C;
                _txPacket.class_id.PacketClassCode = 0x03E3;

                //_txPacket.payload = new float[256];
                _txPacket.payload = new byte[tx_data.Length];
            }

            int samples_sent = 0;

            while (samples_sent < tx_data.Length)
            {
                // how many samples should we send?
                //int num_samples_to_send = Math.Min(256, tx_data.Length - samples_sent);
                int num_samples_to_send = Math.Min(tx_data.Length, tx_data.Length - samples_sent);
                _txPacket.payload = new byte[tx_data.Length];
                //int num_samples_to_send = tx_data.Length;

                // copy the incoming data into the packet payload
                Array.Copy(tx_data, samples_sent, _txPacket.payload, 0, num_samples_to_send);

                // set the length of the packet
                // packet_size is the 32 bit word length?
                _txPacket.header.packet_size = (ushort)Math.Ceiling((double)num_samples_to_send / 4.0 + 7.0); // 7*4=28 bytes of Vita overhead

                try
                {
                    // send the packet to the radio
                    _radio.VitaSock.SendUDP(_txPacket.ToBytesTX());
                }
                catch (Exception e)
                {
                    Debug.WriteLine("OpusStream: AddTXData sendTo() exception = " + e.ToString());
                }
                // bump the packet count
                _txPacket.header.packet_count = (byte)((_txPacket.header.packet_count + 1) % 16);

                // adjust the samples sent
                samples_sent += num_samples_to_send;
            }
        }

        public void StatusUpdate(string s)
        {
            bool set_radio_ack = false;
            string[] words = s.Split(' ');

            foreach (string kv in words)
            {
                string[] tokens = kv.Split('=');
                if (tokens.Length != 2)
                {
                    Debug.WriteLine("OpusStream::StatusUpdate: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key.ToLower())
                {
                    case "rx_on":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _remoteRxOn = Convert.ToBoolean(temp);
                            RaisePropertyChanged("RemoteRxOn");
                        }
                        break;

                    case "tx_on":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _radio.RemoteTxOn = Convert.ToBoolean(temp);

                            if (!_radioAck)
                                set_radio_ack = true;
                        }
                        break;

                    case "ip":
                        {
                            IPAddress temp = null;
                            bool b = IPAddress.TryParse(value, out temp);
                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid ip address (" + kv + ")");
                                continue;
                            }

                            _ip = temp;
                            RaisePropertyChanged("IP");

                            //if (!_radioAck)
                            //    set_radio_ack = true;
                        }
                        break;

                    case "port":
                        {
                            ushort temp;
                            bool b = ushort.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _port = (int)temp;
                            RaisePropertyChanged("Port");
                        }
                        break;

                    case "tx_stream":
                        {
                            uint temp;
                            bool b = uint.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _txStreamID = temp;
                            RaisePropertyChanged("TXStreamID");
                        }
                        break;

                    case "opus_rx_stream_stopped":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("OpusStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _opusRXStreamStopped = Convert.ToBoolean(temp); 
                            RaisePropertyChanged("OpusRXStreamStopped");
                        }
                        break;

                    default:
                        Debug.WriteLine("OpusStream::StatusUpdate: Key not parsed (" + kv + ")");
                        break;
                }
            }

            if (set_radio_ack)
            {
                RadioAck = true;
                _radio.OnOpusStreamAdded(this);

                OpusStartUpdateRXRateThread();
            }
        }
    }
}
