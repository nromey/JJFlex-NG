// ****************************************************************************
///*!	\file DisplayMarker.cs
// *	\brief Represents a single Display Marker (band plan segment or user marker)
// *
// *	\copyright	Copyright 2026 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2026-02-11
// */
// ****************************************************************************

using System;
using System.Diagnostics;
using Flex.UiWpfFramework.Mvvm;
using Flex.Util;

namespace Flex.Smoothlake.FlexLib;

public class DisplayMarker : ObservableObject
{
    private Radio _radio;

    internal DisplayMarker(Radio radio, string group, uint id)
    {
        this._radio = radio;
        this._group = group;
        this._id = id;
    }

    private string _group;
    public string Group
    {
        get { return _group; }
    }

    private uint _id;
    public uint ID
    {
        get { return _id; }
    }

    private string _label;
    public string Label
    {
        get { return _label; }
        set
        {
            if (_label != value)
            {
                _label = value;
                RaisePropertyChanged(nameof(Label));
            }
        }
    }

    private double _startFreq;
    public double StartFreq
    {
        get { return _startFreq; }
        set
        {
            if (_startFreq != value)
            {
                _startFreq = value;
                RaisePropertyChanged(nameof(StartFreq));
            }
        }
    }

    private double _stopFreq;
    public double StopFreq
    {
        get { return _stopFreq; }
        set
        {
            if (_stopFreq != value)
            {
                _stopFreq = value;
                RaisePropertyChanged(nameof(StopFreq));
            }
        }
    }

    private string _colorName;
    public string ColorName
    {
        get { return _colorName; }
        set
        {
            if (_colorName != value)
            {
                _colorName = value;
                RaisePropertyChanged(nameof(ColorName));
            }
        }
    }

    private uint _opacity;
    public uint Opacity
    {
        get { return _opacity; }
        set
        {
            if (_opacity != value)
            {
                _opacity = value;
                RaisePropertyChanged(nameof(Opacity));
            }
        }
    }

    public bool IsIARUGroup
    {
        get { return _group != null && _group.StartsWith("IARU", StringComparison.OrdinalIgnoreCase); }
    }

    public override string ToString()
    {
        return $"DisplayMarker: {Group}/{ID} \"{Label}\" {StartFreq:F6}-{StopFreq:F6} MHz {ColorName}";
    }

    public void StatusUpdate(string s)
    {
        string[] words = s.Split(' ');

        foreach (string kv in words)
        {
            string[] tokens = kv.Split('=');
            if (tokens.Length != 2)
            {
                Debug.WriteLine($"DisplayMarker::StatusUpdate: Invalid key/value pair ({kv})");
                continue;
            }

            string key = tokens[0];
            string value = tokens[1];

            switch (key.ToLower())
            {
                case "label":
                    {
                        _label = value.Trim('"');
                        RaisePropertyChanged(nameof(Label));
                        break;
                    }
                case "start_freq":
                    {
                        double temp;
                        bool b = StringHelper.TryParseDouble(value, out temp);
                        if (!b)
                        {
                            Debug.WriteLine($"DisplayMarker::StatusUpdate - start_freq: Invalid value ({kv})");
                            continue;
                        }

                        _startFreq = temp;
                        RaisePropertyChanged(nameof(StartFreq));
                        break;
                    }
                case "stop_freq":
                    {
                        double temp;
                        bool b = StringHelper.TryParseDouble(value, out temp);
                        if (!b)
                        {
                            Debug.WriteLine($"DisplayMarker::StatusUpdate - stop_freq: Invalid value ({kv})");
                            continue;
                        }

                        _stopFreq = temp;
                        RaisePropertyChanged(nameof(StopFreq));
                        break;
                    }
                case "color":
                    {
                        _colorName = value;
                        RaisePropertyChanged(nameof(ColorName));
                        break;
                    }
                case "opacity":
                    {
                        uint temp;
                        bool b = uint.TryParse(value, out temp);
                        if (!b)
                        {
                            Debug.WriteLine($"DisplayMarker::StatusUpdate - opacity: Invalid value ({kv})");
                            continue;
                        }

                        _opacity = temp;
                        RaisePropertyChanged(nameof(Opacity));
                        break;
                    }
                default:
                    {
                        Debug.WriteLine($"DisplayMarker::StatusUpdate: Unknown key ({key})");
                        break;
                    }
            }
        }
    }
}
