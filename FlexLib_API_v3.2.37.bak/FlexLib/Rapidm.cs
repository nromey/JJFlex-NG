// ****************************************************************************
///*!	\file RapidM.cs
// *	\brief Represents a RapidM modem interface
// *
// *	\copyright	Copyright 2020 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2020-11-11
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using Flex.UiWpfFramework.Mvvm;


namespace Flex.Smoothlake.FlexLib
{
    public class RapidM : ObservableObject
    {
        private Radio _radio;

        public RapidM(Radio radio)
        {
            _radio = radio;
        }

        #region Properties

        private string _waveform;
        /// <summary>
        /// The RapidM Waveform Mode
        /// </summary>
        public string Waveform
        {
            get { return _waveform; }
            set
            {
                if (_waveform != value)
                {
                    _waveform = value;
                    //SendCommand("rapidm psk set wf=" + _waveform);
                    RaisePropertyChanged("Waveform");
                }
            }
        }

        private string _rate;
        /// <summary>
        /// The RapidM baud rate
        /// </summary>
        public string Rate
        {
            get { return _rate; }
            set
            {
                if (_rate != value)
                {
                    _rate = value;
                    //SendCommand("rapidm psk set rate=" + _rate);
                    RaisePropertyChanged("Rate");
                }
            }
        }

        private string _interleaver;
        /// <summary>
        /// The RapidM Interleaver
        /// </summary>
        public string Interleaver
        {
            get { return _interleaver; }
            set
            {
                if (_interleaver != value)
                {
                    _interleaver = value;
                    //SendCommand("rapidm psk set il=" + _interleaver);
                    RaisePropertyChanged("Interleaver");
                }
            }
        }

        private string _snr;
        /// <summary>
        /// The RapidM SNR
        /// </summary>
        public string SNR
        {
            get { return _snr; }
        }

        private string _ber;
        /// <summary>
        /// The RapidM bit error rate
        /// </summary>
        public string BER
        {
            get { return _ber; }
        }

        #endregion

        /// <summary>
        /// The delegate event handler for the RapidmMessageReceived event
        /// </summary>
        /// <param name="message">The string that was received</param>
        public delegate void MessageReceivedEventHandler(string message);
        /// <summary>
        /// This event is raised when a RapidM message is received
        /// </summary>
        public event MessageReceivedEventHandler MessageReceived;

        private void OnMessageReceived(string message)
        {
            if (MessageReceived != null)
                MessageReceived(message);
        }

        /// <summary>
        /// Sets up the RapidM waveform, rate and interleaver
        /// </summary>
        public void Configure()
        {
            _radio.SendCommand("rapidm psk set wf=" + _waveform + " rate=" + _rate + " il=" + _interleaver);
        }

        public void SendMessage(string message)
        {
            string encoded_string = message.Replace(' ', '\u007f');
            _radio.SendCommand("rapidm tx_message " + encoded_string);
        }

        internal void ParseStatus(string s)
        {
            string[] words = s.Split(' ');

            if (words.Length < 2)
            {
                Debug.WriteLine("RapidM::ParseRapidmStatus: Too few words -- min 2 (" + words + ")");
                return;
            }

            // handle non key/value pair type statuses
            if (words[0] == "rx_message")
            {
                string encoded_message = s.Substring("rx_message ".Length); // strip off the rx_message
                string message = encoded_message.Replace('\u007f', ' '); // decode the spaces
                OnMessageReceived(message); // fire the event
                return;
            }

            foreach (string kv in words)
            {
                string[] tokens = kv.Split('=');
                if (tokens.Length != 2)
                {
                    Debug.WriteLine("RapidM::ParseRapidmStatus: Invalid key/value pair (" + kv + ")");
                    continue;
                }

                string key = tokens[0];
                string value = tokens[1];

                switch (key.ToLower())
                {
                    case "wf":
                        {
                            if (_waveform == value) continue;

                            _waveform = value;
                            RaisePropertyChanged("Waveform");
                        }
                        break;

                    case "rate":
                        {
                            if (_rate == value) continue;

                            _rate = value;
                            RaisePropertyChanged("Rate");
                        }
                        break;

                    case "il": // interleaver
                        {
                            if (_interleaver == value) continue;

                            _interleaver = value;
                            RaisePropertyChanged("Interleaver");
                        }
                        break;

                    case "snr": // signal to noise ratio
                        {
                            if (_snr == value) continue;

                            _snr = value;
                            RaisePropertyChanged("SNR");
                        }
                        break;

                    case "ber": // bit error rate
                        {
                            if (_ber == value) continue;

                            _ber = value;
                            RaisePropertyChanged("BER");
                        }
                        break;
                }
            }
        }
    }
}
