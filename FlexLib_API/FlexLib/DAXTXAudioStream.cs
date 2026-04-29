// ****************************************************************************
///*!	\file DAXTXAudioStream.cs
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
using System.Globalization;
using System.Diagnostics;
using System.Net;
using Flex.Smoothlake.FlexLib.Interface;
using Flex.UiWpfFramework.Mvvm;
using Flex.Smoothlake.Vita;
using Flex.Util;

namespace Flex.Smoothlake.FlexLib
{
    public class DAXTXAudioStream : ObservableObject, IDaxTxStream
    {
        private const int TX_SAMPLES_PER_PACKET = 128; // Samples can be mono-int or stereo-float
        private Radio _radio;
        private bool _closing = false;

        internal bool Closing
        {
            set { _closing = value; }
        }

        public DAXTXAudioStream(Radio radio)
        {
            _radio = radio;
        }

        private uint _clientHandle;
        public uint ClientHandle
        {
            get { return _clientHandle; }
            set { _clientHandle = value; }
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

        private bool _transmit = false;
        public bool Transmit
        {
            get { return _transmit; }
            set
            {
                if (_transmit != value)
                {
                    _transmit = value;
                    RaisePropertyChanged("Transmit");
                }
            }
        }

        /// <summary>
        /// Request or yield DAX TX ownership from the radio. Used in multiFLEX
        /// when another client currently has TX and this client wants to take it
        /// back, or when this client wants to yield TX to another client.
        /// </summary>
        public void RequestTX(bool tx)
        {
            _radio.SendCommand($"stream set 0x{_txStreamID:X} tx={Convert.ToByte(tx)}");
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
                    _txGain = new_gain;
                    RaisePropertyChanged("TXGain");
                }
                else if (new_gain != value)
                {
                    RaisePropertyChanged("TXGain");
                }
            }
        }

        public int Gain
        {
            get => TXGain;
            set
            {
                if (_txGain == value)
                    return;

                TXGain = value;
                RaisePropertyChanged("Gain");
            }
        }

        public void Close()
        {
            Debug.WriteLine("TXAudioStream::Close (0x" + _txStreamID.ToString("X") + ")");
            _closing = true;
            _radio.SendCommand("stream remove 0x" + _txStreamID.ToString("X"));
            _radio.RemoveDAXTXAudioStream(_txStreamID);
        }
        
        private VitaIFDataPacket _txPacket;

        // Pre-allocated buffers to avoid per-call allocations
        private readonly Int16[] _int16Buffer = new Int16[TX_SAMPLES_PER_PACKET];
        private readonly float[] _stereoPayload = new float[TX_SAMPLES_PER_PACKET * 2];
        private byte[] _sendBufferReduced;
        private byte[] _sendBufferFull;

        private void EnsureTxPacketInitialized()
        {
            if (_txPacket != null) return;

            _txPacket = new VitaIFDataPacket();
            _txPacket.Header.pkt_type = VitaPacketType.IFDataWithStream;
            _txPacket.Header.c = true;
            _txPacket.Header.t = false;
            _txPacket.Header.tsi = VitaTimeStampIntegerType.Other;
            _txPacket.Header.tsf = VitaTimeStampFractionalType.SampleCount;

            _txPacket.StreamId = _txStreamID;
            _txPacket.ClassId.OUI = 0x001C2D;
            _txPacket.ClassId.InformationClassCode = 0x534C;

            // Pre-assign reusable payload arrays
            _txPacket.payload_int16 = _int16Buffer;
            _txPacket.payload = _stereoPayload;

            // Pre-calculate and allocate send buffers for both modes
            _txPacket.ClassId.PacketClassCode = 0x0123;
            _txPacket.Header.packet_size = (ushort)((TX_SAMPLES_PER_PACKET * sizeof(Int16) / 4) + 7);
            _sendBufferReduced = new byte[_txPacket.CalculatePacketSize(use_int16_payload: true)];

            _txPacket.ClassId.PacketClassCode = 0x03E3;
            _txPacket.Header.packet_size = (ushort)(TX_SAMPLES_PER_PACKET * 2 + 7);
            _sendBufferFull = new byte[_txPacket.CalculatePacketSize(use_int16_payload: false)];
        }

        public void AddTXData(float[] tx_data_stereo, bool sendReducedBW = false)
        {
            // skip this if we are not the DAX TX Client
            if (!_transmit) return;

            EnsureTxPacketInitialized();

            // Send entire passed in buffer but warn if not a correct multiple
            if (tx_data_stereo.Length != TX_SAMPLES_PER_PACKET * 2) // * 2 for stereo
            {
                Debug.WriteLine("Invalid number of samples passed in. Expecting {0} samples of left/right float pairs", TX_SAMPLES_PER_PACKET);
                return;
            }

            if (sendReducedBW)
            {
                _txPacket.ClassId.PacketClassCode = 0x0123;

                // set the length of the packet -- note this is in 4 byte word units
                _txPacket.Header.packet_size = (ushort)((TX_SAMPLES_PER_PACKET * sizeof(Int16) / 4) + 7); // 7*4=28 bytes of Vita overhead

                // De-interleave, clamp, and convert to Int16 in a single pass
                for (int i = 0; i < TX_SAMPLES_PER_PACKET; i++)
                {
                    float sample = tx_data_stereo[i * 2];
                    if (sample > 1.0f)
                        sample = 1.0f;
                    else if (sample < -1.0f)
                        sample = -1.0f;

                    _int16Buffer[i] = (Int16)(sample * 32767);
                }

                // Write directly into pre-allocated send buffer
                _txPacket.WriteBytes(_sendBufferReduced, use_int16_payload: true);
                try
                {
                    _radio.VitaSock.SendUdp(_sendBufferReduced, _sendBufferReduced.Length);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to send reduced bandwidth packet: {ex}");
                }
            }
            else
            {
                _txPacket.ClassId.PacketClassCode = 0x03E3;

                // set the length of the packet -- note this is in 4 byte word units
                _txPacket.Header.packet_size = (ushort)(TX_SAMPLES_PER_PACKET * 2 + 7); // 7*4=28 bytes of Vita overhead

                // Copy the incoming data into the pre-allocated payload
                Buffer.BlockCopy(tx_data_stereo, 0, _stereoPayload, 0, TX_SAMPLES_PER_PACKET * 2 * sizeof(float));

                // Write directly into pre-allocated send buffer
                _txPacket.WriteBytes(_sendBufferFull, use_int16_payload: false);
                try
                {
                    _radio.VitaSock.SendUdp(_sendBufferFull, _sendBufferFull.Length);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to send full bandwidth packet: {ex}");
                }           
            }

            // bump the packet count
            _txPacket.Header.packet_count = (byte)((_txPacket.Header.packet_count + 1) % 16);
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
                    Debug.WriteLine("DAXTXAudioStream::StatusUpdate: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key.ToLower())
                {
                    case "client_handle":
                        {
                            uint temp;
                            bool b = StringHelper.TryParseInteger(value, out temp);

                            if (!b) continue;

                            _clientHandle = temp;
                            RaisePropertyChanged("ClientHandle");

                            if (!_radioAck)
                                set_radio_ack = true;
                        }
                        break;

                    case "tx":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b || temp > 1)
                            {
                                Debug.WriteLine($"DAXTXAudioStream::StatusUpdate: Invalid value ({kv})");
                                continue;
                            }

                            bool new_transmit_state = Convert.ToBoolean(temp);
                            if (_transmit == new_transmit_state)
                                continue;

                            _transmit = new_transmit_state;
                            RaisePropertyChanged("Transmit");
                        }
                        break;

                    default:
                        Debug.WriteLine("DAXTXAudioStream::StatusUpdate: Key not parsed (" + kv + ")");
                        break;
                }
            }

            if (set_radio_ack)
            {
                RadioAck = true;
                _radio.OnDAXTXAudioStreamAdded(this);                
            }
        }
    }
}
