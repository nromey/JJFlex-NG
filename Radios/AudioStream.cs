// ****************************************************************************
///*!	\file AudioStream.cs
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
    public class AudioStream : ObservableObject
    {
        private Radio _radio;
        private bool _closing = false;
    
        internal bool Closing
        {
            set { _closing = value; }
        }

        public AudioStream(Radio radio, int dax_channel)
        {
            this._radio = radio;
            this._daxChannel = dax_channel;

            _slice = _radio.FindSliceByDAXChannel(dax_channel);
        }

        private uint _rxStreamId;
        public uint RXStreamID
        {
            get { return _rxStreamId; }
            internal set { _rxStreamId = value; }
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

        private int _daxChannel;
        public int DAXChannel
        {
            get { return _daxChannel; }
            internal set
            {
                if (_daxChannel != value)
                {
                    _daxChannel = value;
                    RaisePropertyChanged("DAXChannel");

                    if(_radio != null)
                        Slice = _radio.FindSliceByDAXChannel(_daxChannel);
                }
            }
        }

        private Slice _slice;
        public Slice Slice
        {
            get { return _slice; }
            internal set
            {
                if (_slice != value)
                {
                    _slice = value;
                    RaisePropertyChanged("Slice");

                    // resend the RX Gain for the new slice
                    if (_slice != null)
                    {
                        int gain = _rxGain;
                        _rxGain = 0;
                        RXGain = gain;
                    }
                }
            }
        }

        private int _rxGain = 50;
        public int RXGain
        {
            get { return _rxGain; }
            set
            {
                int new_gain = value;

                // check limits
                if (new_gain > 100) new_gain = 100;
                if (new_gain < 0) new_gain = 0;

                if (_rxGain != new_gain)
                {
                    _rxGain = new_gain;
                    if(_slice != null)
                        _radio.SendCommand("audio stream 0x" + _rxStreamId.ToString("X") + " slice " + _slice.Index + " gain " + value);
                    RaisePropertyChanged("RXGain");
                }
                else if (new_gain != value)
                {
                    RaisePropertyChanged("RXGain");
                }
            }
        }

        private double _byteSum = 0;
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

        public bool RequestAudioStreamFromRadio()
        {
            // check to see if this object has already been activated
            if (_radioAck) return false;

            // check to ensure this object is tied to a radio object
            if (_radio == null) return false;

            // check to make sure the radio is connected
            if (!_radio.Connected) return false;

            // send the command to the radio to create the object
            _radio.SendReplyCommand(new ReplyHandler(UpdateStreamID), "stream create dax=" + _daxChannel);

            return true;
        }

        private void UpdateStreamID(int seq, uint resp_val, string s)
        {
            if (resp_val != 0) return;

            bool b = uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _rxStreamId);

            if (!b)
            {
                Debug.WriteLine("AudioStream::UpdateStreamID-Error parsing Stream ID (" + s + ")");
                return;
            }

            _radio.AddAudioStream(this);
        }

        public void Close()
        {
            Debug.WriteLine("AudioStream::Close (0x" + _rxStreamId.ToString("X") + ")");
            _closing = true;
            _radio.SendCommand("stream remove 0x" + _rxStreamId.ToString("X"));
            _radio.RemoveAudioStream(_rxStreamId);
        }

        private int error_count_out_of_order = 0;
        private int error_count_total = 0;
        public int ErrorCount
        {
            get { return error_count_total; }
            set
            {
                if (error_count_total != value)
                {
                    error_count_total = value;
                    RaisePropertyChanged("ErrorCount");
                }
            }
        }

        private int total_count = 0;
        public int TotalCount
        {
            get { return total_count; }
            set
            {
                if (total_count != value)
                {
                    total_count = value;
                    // only raise the property change every 100 packets (performance)
                    if (total_count % 100 == 0) RaisePropertyChanged("TotalCount");
                }
            }
        }


        // note that the "Lost" indicator assumes we only lost a single packet -- this is probably reasonable for "decent" networks
        private void PrintStats()
        {
            int lost = error_count_total - error_count_out_of_order;
            Debug.WriteLine("Audio Stream 0x" + _rxStreamId.ToString("X").PadLeft(8, '0') +
                "-Reversed: " + error_count_out_of_order + " (" + (error_count_out_of_order * 100.0 / total_count).ToString("f2") + ")" +
                "  Lost: " + lost + " (" + (lost * 100.0 / total_count).ToString("f2") + ")" +
                "  Total: " + total_count);
        }

        private const int NOT_INITIALIZED = 99;
        private int last_packet_count = NOT_INITIALIZED;
        internal void AddRXData(VitaIFDataPacket packet)
        {
            TotalCount++;
#if DEBUG_STATS
            if (total_count % 1000 == 0) PrintStats();
#endif

            lock (this)
            {
                _byteSum += packet.Length;
                //Debug.WriteLine("packet.Length: " + packet.Length);
            }

            int packet_count = packet.header.packet_count;

            OnRXDataReady(this, packet.payload);
            // normal case -- this is the next packet we are looking for, or it is the first one
            if (packet_count == (last_packet_count + 1) % 16 || last_packet_count == NOT_INITIALIZED)
            {
                last_packet_count = packet_count;
            }
            else 
            {
                error_count_out_of_order++;
                ErrorCount++;
                last_packet_count = packet_count;
            }
        }

        public delegate void RXDataReadyEventHandler(AudioStream audio_stream, float[] rx_data);
        public event RXDataReadyEventHandler RXDataReady;
        private void OnRXDataReady(AudioStream audio_stream, float[] rx_data)
        {
            if (RXDataReady != null)
                RXDataReady(audio_stream, rx_data);
        }

        private void UpdateRXRate()
        {
            while (!_closing)
            {
                lock (this)
                {
                    _bytesPerSecFromRadio = _byteSum;
                    _byteSum = 0;
                }
                RaisePropertyChanged("BytesPerSecFromRadio");
                //Debug.WriteLine("AudioStream-" + _daxChannel + " Rate: " + (_bytesPerSecFromRadio * 8.0 / 1000.0 / 1000.0).ToString("f3")+ " Mbps");
                Thread.Sleep(1000);
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
                    Debug.WriteLine("AudioStream::StatusUpdate: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key.ToLower())
                {
                    case "dax":
                        {
                            uint temp;
                            bool b = uint.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("AudioStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            DAXChannel = (int)temp;
                        }
                        break;

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
                    case "slice":
                        {
                            byte temp;
                            bool b = byte.TryParse(value, out temp);

                            if (!b)
                            {
                                Debug.WriteLine("AudioStream::StatusUpdate: Invalid value (" + kv + ")");
                                continue;
                            }

                            _slice = _radio.FindSliceByIndex(temp);
                            RaisePropertyChanged("Slice");
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
                _radio.OnAudioStreamAdded(this);                

                Thread t = new Thread(new ThreadStart(UpdateRXRate));
                t.Name = "AudioStream UpdateRXRate Thread";
                t.IsBackground = true;
                t.Priority = ThreadPriority.Normal;
                t.Start();
            }
        }
    }
}
