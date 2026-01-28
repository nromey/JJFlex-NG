// ****************************************************************************
///*!	\file TXAudioStream.cs
// *	\brief Represents a single TX Audio Stream (narrow, mono)
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
    public class TXAudioStream : ObservableObject
    {
        private const int TX_SAMPLES_PER_PACKET = 128; // Samples can be mono-int or stereo-float
        private Radio _radio;
        private bool _closing = false;

        internal bool Closing
        {
            set { _closing = value; }
        }

        public TXAudioStream(Radio radio)
        {
            _radio = radio;
        }

        private uint _txStreamID;
        public uint TXStreamID
        {
            get { return _txStreamID; }
            internal set
            {
                if (_txStreamID != value)
                {
                    _txStreamID = value;
                    RaisePropertyChanged("TXStreamID");
                }
            }
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
    
        private bool _transmit;
        public bool Transmit
        {
            get { return _transmit; }
            set
            {
                if (_transmit != value)
                {
                    _transmit = value;
                    _radio.SendCommand("dax tx " + Convert.ToByte(_transmit));
                    RaisePropertyChanged("Transmit");
                }
            }
        }

        private int _txGain;
        public int TXGain
        {
            get { return _txGain; }
            set
            {
                int new_gain = value;

                // check limits
                if (new_gain > 100) new_gain = 100;
                if (new_gain < 0) new_gain = 0;

                if (_txGain != new_gain)
                {
                    _txGain = value;
                    RaisePropertyChanged("TXGain");
                }
                else if (new_gain != value)
                {
                    RaisePropertyChanged("TXGain");
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

        public bool RequestTXAudioStreamFromRadio()
        {
            // check to see if this object has already been activated
            if (_radioAck) return false;

            // check to ensure this object is tied to a radio object
            if (_radio == null) return false;

            // check to make sure the radio is connected
            if (!_radio.Connected) return false;

            // send the command to the radio to create the object
            _radio.SendReplyCommand(new ReplyHandler(UpdateStreamID), "stream create daxtx");

            return true;
        }

        private void UpdateStreamID(int seq, uint resp_val, string s)
        {
            if (resp_val != 0) return;

            bool b = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _txStreamID);

            if (!b)
            {
                Debug.WriteLine("TXAudioStream::UpdateStreamID-Error parsing Stream ID (" + s + ")");
                return;
            }

            _radio.AddTXAudioStream(this);
        }

        public void Close()
        {
            Debug.WriteLine("TXAudioStream::Close (0x" + _txStreamID.ToString("X") + ")");
            _closing = true;
            _radio.SendCommand("stream remove 0x" + _txStreamID.ToString("X"));
            _radio.RemoveTXAudioStream(_txStreamID);
        }
        
        private VitaIFDataPacket _txPacket;
        public void AddTXData(float[] tx_data_stereo, bool sendReducedBW = false)
        {
            // skip this if we are not the DAX TX Client
            if (!_transmit) return;

            if (_txPacket == null)
            {
                _txPacket = new VitaIFDataPacket();
                _txPacket.header.pkt_type = VitaPacketType.IFDataWithStream;
                _txPacket.header.c = true;
                _txPacket.header.t = false;
                _txPacket.header.tsi = VitaTimeStampIntegerType.Other;
                _txPacket.header.tsf = VitaTimeStampFractionalType.SampleCount;

                _txPacket.stream_id = _txStreamID;
                _txPacket.class_id.OUI = 0x001C2D;
                _txPacket.class_id.InformationClassCode = 0x534C;
            }

            if (sendReducedBW)
            {
                _txPacket.class_id.PacketClassCode = 0x0123;
            }
            else
            {
                _txPacket.class_id.PacketClassCode = 0x03E3;
            }

            // Send entire passed in buffer but warn if not a correct multiple
            if (tx_data_stereo.Length != TX_SAMPLES_PER_PACKET * 2) // * 2 for stereo
            {
                Debug.WriteLine("Invalid number of samples passed in. Expecting {0} samples of left/right float pairs", TX_SAMPLES_PER_PACKET);
                return;
            }

            float[] tx_data;

            if (sendReducedBW)
            {
                // Deinterleave since we're only sending MONO
                tx_data = new float[tx_data_stereo.Length / 2];
                for (int i = 0; i < tx_data.Length; i++)
                {
                    tx_data[i] = tx_data_stereo[i * 2];
                }
            }
            else
            {
                tx_data = tx_data_stereo;
            }

            int num_samples_to_send = 0;

            // how many samples should we send?
            if (sendReducedBW)
            {
                num_samples_to_send = TX_SAMPLES_PER_PACKET;

                _txPacket.payload_int16 = new Int16[num_samples_to_send];

                for (int i = 0; i < num_samples_to_send; i++)
                {
                    if (tx_data[i] > 1.0)
                        tx_data[i] = 1.0f;
                    else if (tx_data[i] < -1.0)
                        tx_data[i] = -1.0f;

                    _txPacket.payload_int16[i] = (Int16)(tx_data[i] * 32767);
                }

                // set the length of the packet
                _txPacket.header.packet_size = (ushort)(num_samples_to_send / sizeof(Int16) + 7); // 7*4=28 bytes of Vita overhead
            }
            else
            {
                num_samples_to_send = TX_SAMPLES_PER_PACKET * 2; // *2 for stereo

                _txPacket.payload = new float[num_samples_to_send];

                // copy the incoming data into the packet payload
                Array.Copy(tx_data, 0, _txPacket.payload, 0, num_samples_to_send);

                _txPacket.header.packet_size = (ushort)(num_samples_to_send + 7); // 7*4=28 bytes of Vita overhead
            }

            // send the packet to the radio
            //Debug.WriteLine("sending from channel " + _daxChannel);

            try
            {

                _radio.VitaSock.SendUDP(_txPacket.ToBytes(use_int16_payload: sendReducedBW));
            }
            catch (Exception e)
            {
                Debug.WriteLine("TXAudioStream: AddTXData Exception (" + e.ToString() + ")");
            }
            //Debug.Write("("+num_samples_to_send+")");

            // bump the packet count
            _txPacket.header.packet_count = (byte)((_txPacket.header.packet_count + 1) % 16);
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
                    Debug.WriteLine("TXAudioStream::StatusUpdate: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key.ToLower())
                {
                    case "ip":
                        {
                            IPAddress temp = null;
                            bool b = IPAddress.TryParse(value, out temp);
                            if (!b)
                            {
                                Debug.WriteLine("AudioStream::StatusUpdate: Invalid ip address (" + kv + ")");
                                continue;
                            }

                            _ip = temp;
                            RaisePropertyChanged("IP");

                            if (!_radioAck)
                                set_radio_ack = true;
                        }
                        break;

                    case "port":
                        {
                            ushort temp;
                            bool b = ushort.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("AudioStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _port = (int)temp;
                            RaisePropertyChanged("Port");
                        }
                        break;

                    case "dax_tx":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("AudioStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _transmit = Convert.ToBoolean(temp);
                            RaisePropertyChanged("Transmit");
                        }
                        break;
                    default:
                        Debug.WriteLine("AudioStream::StatusUpdate: Key not parsed (" + kv + ")");
                        break;
                }
            }

            if (set_radio_ack)
            {
                RadioAck = true;
                _radio.OnTXAudioStreamAdded(this);                
            }
        }
    }
}
