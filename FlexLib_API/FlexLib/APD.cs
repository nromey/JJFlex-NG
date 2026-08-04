// ****************************************************************************
///*!	\file APD.cs
// *	\brief APD model
// *
// *	\copyright	Copyright 2025 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// */
// ****************************************************************************

using Flex.UiWpfFramework.Mvvm;
using System;
using System.Collections;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;

namespace Flex.Smoothlake.FlexLib;

public enum APDSamplerPorts
{
    INTERNAL,
    RX_A,
    XVTA,
    RX_B,
    XVTB
}

public record EqualizerStatus(bool enable, string ant, double freq, int rfpower);

public class APD(Radio radio) : ObservableObject
{
    private Radio _radio = radio;
    private static readonly Queue _statusQueue = new();
    private static System.Threading.Timer _statusApplyTimer = null;

    public void Exit()
    {
        // Clean up the timer so that it does not fire again. This can cause stale Radio references to persist and lead
        // to the model not updating correctly in response to new equalizer statuses.
        _statusApplyTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _statusApplyTimer = null;
    }

    public void ParseStatus(string s)
    {
        bool? activeSet = null;
        double freq = double.NaN;
        int rfpower = 0;
        string ant = null;
        string[] words = s.Split(' ');
        if (words.Length == 0) { return; }

        foreach (string kv in words)
        {
            string[] tokens = kv.Split('=');
            if (tokens[0] == "equalizer_reset") // The only key for this status which is a boolean flag - it has no value.
            {
                Debug.WriteLine("Clearing all APD equalizers!");
                EqualizerActive = false;
                continue;
            }
            else if (tokens[0] == "sampler") // The only subfield in an apd status message (as of 14/1/25)
            {
                ParseSamplerStatus(s.Substring("sampler ".Length));
                return;
            }
            else if (tokens.Length != 2)
            {
                if (!string.IsNullOrEmpty(kv)) Debug.WriteLine($"APD::ParseStatus: Invalid key/value pair ({kv})");
                continue;
            }
            string key = tokens[0];
            string value = tokens[1];
            switch (key.ToLower())
            {
                case ("ant"):
                    {
                        ant = value;
                        break;
                    }
                case ("configurable"):
                    {
                        if (!byte.TryParse(value, out var temp))
                        {
                            Debug.WriteLine($"APD::ParseStatus - enable: Invalid value ({kv})");
                            continue;
                        }
                        _configurable = Convert.ToBoolean(temp);
                        RaisePropertyChanged(nameof(Configurable));
                        break;
                    }
                case ("enable"):
                    {
                        if (!byte.TryParse(value, out var temp))
                        {
                            Debug.WriteLine($"APD::ParseStatus - enable: Invalid value ({kv})");
                            continue;
                        }
                        var enabled = Convert.ToBoolean(temp);
                        if (_enabled == enabled)
                        {
                            break;
                        }
                        _enabled = enabled;
                        RaisePropertyChanged(nameof(Enabled));
                        break;
                    }
                case ("equalizer_active"):
                    {
                        if (!byte.TryParse(value, out var temp))
                        {
                            Debug.WriteLine($"APD::ParseStatus - equalizer_active: active value ({kv})");
                            continue;
                        }
                        activeSet = Convert.ToBoolean(temp);
                        break;
                    }
                case ("freq"):
                    {
                        if (!double.TryParse(value, out freq))
                        {
                            Debug.WriteLine($"APD::ParseStatus: Invalid frequency ({value})");
                            freq = double.NaN;
                            continue;
                        }
                        break;
                    }

                case "rfpower":
                    if (!int.TryParse(value, out rfpower))
                    {
                        Debug.WriteLine($"APD::ParseStatus - rfpower: value ({kv}) is invalid");
                        continue;
                    }
                    break;
                
                case "sample_index":
                    if (!int.TryParse(value, out int index))
                    {
                        Debug.WriteLine($"Invalid APD Index: {value}");
                        continue;
                    }
                    if (GatherApdLogs) 
                    {
                        DownloadApdLog(index).SafeFireAndForget();
                    }
                    break;
            }
        }

        // If we have a change to the active status of an equalizer for a given frequency + antenna, see if it applies to one of our slices.
        // Queue APD status messages and check them after an interval, so that rapid movements of the slice don't cause loss of sync.
        if (!(activeSet is null || double.IsNaN(freq) || String.IsNullOrEmpty(ant)))
        {
            QueueEqualizerActiveStatus(activeSet.Value, ant, freq, rfpower);
        }
    }

    private bool _gatherApdLogs;
    public bool GatherApdLogs
    {
        get => _gatherApdLogs;

        set
        {
            if (_gatherApdLogs == value)
            {
                return;
            }

            _gatherApdLogs = value;
            RaisePropertyChanged(nameof(GatherApdLogs));
        }
    }
    
    private readonly string _apdLogDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\FlexRadio Systems\APD Logs\";
    private async Task DownloadApdLog(int index)
    {
        if (!_radio.IsAlphaLicensed)
        {
            return;
        }
            
        Debug.WriteLine($"Downloading APD log at index {index}");

        string portString;

        try
        {
            portString = await _radio.SendCommandAsync($"file download apd_log {index}");
        }
        catch (SmartSdrCommandErrorException ex)
        {
            Debug.WriteLine($"Failed to execute APD Download Command: {ex}");
            return;
        }
            
        if (!int.TryParse(portString, out int port))
        {
            Debug.WriteLine($"Invalid Port Number: {portString}");
            return;
        }

        var server = new TcpListener(IPAddress.Any, port);
        server.Start();

        Debug.WriteLine("Waiting for the APD Log download connection from the radio");
        using TcpClient client = await server.AcceptTcpClientAsync();
            
        Debug.WriteLine("Got a TCP connection from the radio");

        using NetworkStream stream = client.GetStream();

        Debug.WriteLine($"Downloading APD index {index}");
        Directory.CreateDirectory(_apdLogDirectory);
        var apdLogFile = $@"{_apdLogDirectory}\apd_log-{index}.zip";
        using FileStream outputFile = File.Open(apdLogFile, FileMode.Create);
        await stream.CopyToAsync(outputFile);

        Debug.WriteLine($"APD index {index} complete");

        server.Stop();
    }

    public void EqualizerActiveStatusApplyTimerTaskFunction(object state)
    {
        while (_statusQueue.Count != 0)
        {
            EqualizerStatus temp = (EqualizerStatus)_statusQueue.Dequeue();
            Debug.WriteLine($"Parsing queued eq status - enable={temp.enable}, ant={temp.ant}, freq={temp.freq}.");
            ApplyEqualizerActiveStatus(temp.enable, temp.ant, temp.freq, temp.rfpower);
        }
    }

    private void ApplyEqualizerActiveStatus(bool enable, string ant, double freq, int rfpower)
    {
        if (ant == null || _radio == null || _radio.SliceList == null)
        {
            return;
        }

        // If we have a transmit-enabled slice, check that for a match on antenna/frequency.
        // Or just use the current active slice.
        Slice temp = _radio.SliceList.FirstOrDefault(s => s.IsTransmitSlice) ?? _radio.ActiveSlice;

        if (temp is null)
        {
            Debug.WriteLine($"APD::ApplyEqualizerActiveStatus: No slices to apply to.");
            return;
        }

        // The max precision of the APD frequency given is in Hz, but the slice frequency may be sub-Hz.
        // Round the slice frequency for this comparison.
        double roundedFreq = Math.Round(temp.Freq, 6);
        if (ant.Equals(temp.TXAnt, StringComparison.OrdinalIgnoreCase) && freq == roundedFreq && rfpower == _radio.RFPower)
        {
            Debug.WriteLine($"APD::ApplyEqualizerActiveStatus: Updating APD status for slice {temp.Index}, freq={freq}, ant={ant}, rfpower={rfpower}.");
            if (enable)
            {
                OnEqualizerActiveHeartbeat();
            }
            else
            {
                OnEqualizerCalibratingHeartbeat();
            }
            EqualizerActive = enable;
        }
        else
        {
            Debug.WriteLine($"APD::ApplyEqualizerActiveStatus: No matching slice with freq={freq}, ant={ant}, rfpower={rfpower}. Current freq={temp.Freq}.");
        }
    }

    private void QueueEqualizerActiveStatus(bool enable, string ant, double freq, int rfpower)
    {
        if (null == _statusApplyTimer)
        {
            _statusApplyTimer = new System.Threading.Timer(EqualizerActiveStatusApplyTimerTaskFunction, null, 100, 150);
        }
        EqualizerStatus temp = new(enable, ant, freq, rfpower);
        _statusQueue.Enqueue(temp);
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _radio.SendCommand("apd enable=" + Convert.ToByte(_enabled));
            RaisePropertyChanged(nameof(Enabled));
        }
    }

    private bool _configurable;
    public bool Configurable
    {
        get => _configurable;
        internal set
        {
            if (_configurable == value) return;
            _configurable = value;
            RaisePropertyChanged(nameof(Configurable));
        }
    }

    private bool _equalizerActive;
    public bool EqualizerActive
    {
        get => _equalizerActive;
        set
        {
            if (_equalizerActive == value) return;
            _equalizerActive = value;
            RaisePropertyChanged(nameof(EqualizerActive));
            RaisePropertyChanged(nameof(EqualizerCalibrating));
        }
    }

    public bool EqualizerCalibrating => !EqualizerActive;

    private bool _available;
    public bool Available
    {
        get => _available;
        set
        {
            if (_available == value) return;
            _available = value;
            RaisePropertyChanged(nameof(Available));
        }
    }

    public void EqualizerReset()
    {
        _radio?.SendCommand("apd reset");
    }

    #region Sampling

    public List<string> AvailableSamplerPortListANT1 { get; } = new List<string> { nameof(APDSamplerPorts.INTERNAL) };
    public List<string> AvailableSamplerPortListANT2 { get; } = new List<string> { nameof(APDSamplerPorts.INTERNAL) };
    public List<string> AvailableSamplerPortListXVTA { get; } = new List<string> { nameof(APDSamplerPorts.INTERNAL) };
    public List<string> AvailableSamplerPortListXVTB { get; } = new List<string> { nameof(APDSamplerPorts.INTERNAL) };

    private string _selectedSamplerPortANT1 = nameof(APDSamplerPorts.INTERNAL);
    public string SelectedSamplerPortANT1
    {
        get => _selectedSamplerPortANT1;
        set
        {
            if (value is null || _selectedSamplerPortANT1 == value) return;
            _selectedSamplerPortANT1 = value;
            _radio.SendCommand($"apd sampler tx_ant=ANT1 sample_port={_selectedSamplerPortANT1}");
            RaisePropertyChanged(nameof(SelectedSamplerPortANT1));
        }
    }

    private string _selectedSamplerPortANT2 = nameof(APDSamplerPorts.INTERNAL);
    public string SelectedSamplerPortANT2
    {
        get => _selectedSamplerPortANT2;
        set
        {
            if (value is null || _selectedSamplerPortANT2 == value) return;
            _selectedSamplerPortANT2 = value;
            _radio.SendCommand($"apd sampler tx_ant=ANT2 sample_port={_selectedSamplerPortANT2}");
            RaisePropertyChanged(nameof(SelectedSamplerPortANT2));
        }
    }

    private string _selectedSamplerPortXVTA = nameof(APDSamplerPorts.INTERNAL);
    public string SelectedSamplerPortXVTA
    {
        get => _selectedSamplerPortXVTA;
        set
        {
            if (value is null || _selectedSamplerPortXVTA == value) return;
            _selectedSamplerPortXVTA = value;
            _radio.SendCommand($"apd sampler tx_ant=XVTA sample_port={_selectedSamplerPortXVTA}");
            RaisePropertyChanged(nameof(SelectedSamplerPortXVTA));
        }
    }

    private string _selectedSamplerPortXVTB = nameof(APDSamplerPorts.INTERNAL);
    public string SelectedSamplerPortXVTB
    {
        get => _selectedSamplerPortXVTB;
        set
        {
            if (value is null || _selectedSamplerPortXVTB == value) return;
            _selectedSamplerPortXVTB = value;
            _radio.SendCommand($"apd sampler tx_ant=XVTB sample_port={_selectedSamplerPortXVTB}");
            RaisePropertyChanged(nameof(SelectedSamplerPortXVTB));
        }
    }

    void ParseSamplerStatus(string status)
    {
        string[] tokens = status.Split(' ');
        string txAnt = null;
        string currentSampler = null;
        string[] availableSamplers = Array.Empty<string>();

        if (tokens.Length == 0)
        {
            return;
        }

        foreach (var token in tokens)
        {
            string[] kv = token.Split('=');
            if (kv.Length != 2)
            {
                if(!string.IsNullOrEmpty(token)) Trace.WriteLine($"Invalid key-value pair: {token}");
                continue;
            }

            string key = kv[0];
            string val = kv[1];

            switch (key.ToLower())
            {
                case "tx_ant":
                    if (!string.IsNullOrEmpty(val)) txAnt = val;
                    else
                    {
                        Trace.WriteLine("Empty tx_ant value.");
                        Trace.WriteLine("Expected: apd sampler tx_ant=<ANT1|ANT2|XVTA|XVTB> " +
                            "selected_sampler=<INVALID|XVTA|RX_A|XVTB|RX_B>" +
                            "[valid_samplers=<XVTA|RX_A/XVTB|RX_B>]");
                        return;
                    }
                    break;

                case "selected_sampler":
                    if (!string.IsNullOrEmpty(val)) currentSampler = val;
                    else
                    {
                        Trace.WriteLine("Empty selected_sampler value.");
                        Trace.WriteLine("Expected: apd sampler tx_ant=<ANT1|ANT2|XVTA|XVTB> " +
                            "selected_sampler=<INVALID|XVTA|RX_A|XVTB|RX_B>" +
                            "[valid_samplers=<XVTA|RX_A/XVTB|RX_B>]");
                        return;
                    }
                    break;

                case "valid_samplers":
                {
                    string[] samplers = val.Split(',');
                    if (samplers.Length > 0) availableSamplers = samplers;
                    else
                    {
                        Trace.WriteLine("No valid_samplers list");
                        continue;
                    }
                }
                    break;
            }
        }

        // Check for a change in available sampler ports (optional argument)
        if (availableSamplers.Length > 0)
        {
            ChangeAvailableSamplerList(txAnt, availableSamplers);
        }

        // Try to change selected sampler port
        switch (txAnt.ToLower())
        {
            case "ant1":
                _selectedSamplerPortANT1 = AvailableSamplerPortListANT1.Contains(currentSampler)
                    ? currentSampler : nameof(APDSamplerPorts.INTERNAL);

                RaisePropertyChanged(nameof(SelectedSamplerPortANT1));
                break;

            case "ant2":
                _selectedSamplerPortANT2 = AvailableSamplerPortListANT2.Contains(currentSampler)
                    ? currentSampler : nameof(APDSamplerPorts.INTERNAL);

                RaisePropertyChanged(nameof(SelectedSamplerPortANT2));
                break;

            case "xvta":
                _selectedSamplerPortXVTA = AvailableSamplerPortListXVTA.Contains(currentSampler)
                    ? currentSampler : nameof(APDSamplerPorts.INTERNAL);

                RaisePropertyChanged(nameof(SelectedSamplerPortXVTA));
                break;

            case "xvtb":
                _selectedSamplerPortXVTB = AvailableSamplerPortListXVTB.Contains(currentSampler)
                    ? currentSampler : nameof(APDSamplerPorts.INTERNAL);

                RaisePropertyChanged(nameof(SelectedSamplerPortXVTB));
                break;

            default:
                Trace.WriteLine($"Unknown tx_ant value: {txAnt}");
                break;
        }
    }

    private void ChangeAvailableSamplerList(string tx_ant, string[] samplers)
    {
        if (string.IsNullOrEmpty(tx_ant))
        {
            Trace.WriteLine("No entry for transmit antenna");
            return;
        }

        switch (tx_ant.ToLower())
        {
            case "ant1":
                AvailableSamplerPortListANT1.Clear();
                AvailableSamplerPortListANT1.Add(nameof(APDSamplerPorts.INTERNAL));
                foreach (var port in samplers)
                {
                    AvailableSamplerPortListANT1.Add(port);
                }

                RaisePropertyChanged(nameof(AvailableSamplerPortListANT1));
                break;

            case "ant2":
                AvailableSamplerPortListANT2.Clear();
                AvailableSamplerPortListANT2.Add(nameof(APDSamplerPorts.INTERNAL));
                foreach (var port in samplers)
                {
                    AvailableSamplerPortListANT2.Add(port);
                }

                RaisePropertyChanged(nameof(AvailableSamplerPortListANT2));
                break;

            case "xvta":
                AvailableSamplerPortListXVTA.Clear();
                AvailableSamplerPortListXVTA.Add(nameof(APDSamplerPorts.INTERNAL));
                foreach (var port in samplers)
                {
                    AvailableSamplerPortListXVTA.Add(port);
                }

                RaisePropertyChanged(nameof(AvailableSamplerPortListXVTA));
                break;

            case "xvtb":
                AvailableSamplerPortListXVTB.Clear();
                AvailableSamplerPortListXVTB.Add(nameof(APDSamplerPorts.INTERNAL));
                foreach (var port in samplers)
                {
                    AvailableSamplerPortListXVTB.Add(port);
                }

                RaisePropertyChanged(nameof(AvailableSamplerPortListXVTB));
                break;
        }
    }

    #endregion
    #region Events

    /// <summary>
    /// Delegate event handler for the EqualizerHeartbeat event
    /// </summary>
    public delegate void EqualizerActiveHeartbeatEventHandler();
    /// <summary>
    /// This event is raised when an equalizer_active=1 status is received that matches the current transmit slice.
    /// </summary>
    public event EqualizerActiveHeartbeatEventHandler EqualizerActiveHeartbeat;

    private void OnEqualizerActiveHeartbeat()
    {
        if (EqualizerActiveHeartbeat != null)
            EqualizerActiveHeartbeat();
    }

    /// <summary>
    /// Delegate event handler for the EqualizerCalibratingHeartbeat event
    /// </summary>
    public delegate void EqualizerCalibratingHeartbeatEventHandler();
    /// <summary>
    /// This event is raised when an equalizer_active=0 status is received that matches the current transmit slice.
    /// </summary>
    public event EqualizerCalibratingHeartbeatEventHandler EqualizerCalibratingHeartbeat;

    private void OnEqualizerCalibratingHeartbeat()
    {
        if (EqualizerCalibratingHeartbeat != null)
            EqualizerCalibratingHeartbeat();
    }
    #endregion
}
