// ****************************************************************************
///*!	\file UsbLdpaCable.cs
// *	\brief Represents a single LDPA USB Cable
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-10-03
// *	\author Abed Haque, AB5ED
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace Flex.Smoothlake.FlexLib
{
    public class UsbLdpaCable : UsbCable, IUsbLdpaCable
    {
        public UsbLdpaCable(Radio radio, string serial_number)
            : base(radio, serial_number)
        {
            _cableType = UsbCableType.LDPA;
        }

        private UsbCableFreqSource _source = UsbCableFreqSource.None;
        public UsbCableFreqSource Source
        {
            get { return _source; }
            set
            {
                if (_source != value)
                {
                    _source = value;

                    if (_radio != null)
                        _radio.SendCommand("usb_cable set " + _serialNumber + " source=" + UsbCableFreqSourceToString(_source));
                }
            }
        }

        private LdpaBand _band;
        public LdpaBand Band
        {
            get
            {
                return _band;
            }
            set
            {
                if (_band != value)
                {
                    _band = value;

                    string bandStr = "0";

                    if (_band == LdpaBand.LDPA_2m)
                        bandStr = "2";
                    else if (_band == LdpaBand.LDPA_4m)
                        bandStr = "4";

                    if (_radio != null)
                        _radio.SendCommand("usb_cable set " + _serialNumber + " band=" + bandStr);
                }

            }
        }

        private bool _isPreampOn;
        public bool IsPreampOn
        {
            get
            {
                return _isPreampOn;
            }
            set
            {
                if (_isPreampOn != value)
                {
                    _isPreampOn = value;

                    if (_radio != null)
                        _radio.SendCommand("usb_cable set " + _serialNumber + " preamp=" + Convert.ToByte(_isPreampOn));
                }
            }
        }


        internal override void ParseTypeSpecificStatus(int bitNumber, string key, string value)
        {
            switch (key)
            {
                case "band":
                    {
                        _band = StringToLdpaBand(value);
                        RaisePropertyChanged("Band");
                    }
                    break;

                case "preamp":
                    {
                        byte temp;
                        bool b = byte.TryParse(value, out temp);

                        if (!b)
                        {
                            Debug.WriteLine("UsbLdpaCable::ParseStatus - preamp: Invalid value (" + value + ")");
                            break;
                        }

                        _isPreampOn = Convert.ToBoolean(temp);
                        RaisePropertyChanged("IsPreampOn");
                    }
                    break;
            }
        }

        private LdpaBand StringToLdpaBand(string s)
        {
            LdpaBand ret_val = LdpaBand.LDPA_2m;

            switch (s.ToLower().Replace("m", ""))
            {
                case "2": ret_val = LdpaBand.LDPA_2m; break;
                case "4": ret_val = LdpaBand.LDPA_4m; break;
            }

            return ret_val;
        }
    }
}
