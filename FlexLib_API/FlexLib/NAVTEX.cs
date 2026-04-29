/// \file NAVTEX.cs
/// \brief NAVTEX waveform client model
///
/// \copyright Unpublished software of FlexRadio Systems (c) 2025 FlexRadio Systems
///
/// Unauthorized use, duplication or distribution of this software is strictly prohibited by law.
///

using Flex.UiWpfFramework.Mvvm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;

namespace Flex.Smoothlake.FlexLib;
public class NAVTEX : ObservableObject
{
    private Radio _radio;
    private List<NAVTEXMsg> _msgs;
    public List<NAVTEXMsg> Msgs => _msgs;
    private Dictionary<int, NAVTEXMsg> _pendingMsgs;
    private Object _msgsLock = new Object();

    private NAVTEXStatus _status;
    public NAVTEXStatus Status
    {
        get => _status;
        set
        {
            if (value == Status) return;
            _status = value;
            RaisePropertyChanged(nameof(Status));
        }
    }

    public NAVTEX(Radio radio)
    {
        _radio = radio;
        Status = NAVTEXStatus.Inactive;
        _msgs = new List<NAVTEXMsg>();
        _pendingMsgs = new Dictionary<int, NAVTEXMsg>();
    }

    public bool TryToggleNAVTEX(uint broadcastFreqHz = NAVTEX.INTERNATIONAL_BROADCAST_FREQ_HZ)
    {
        if (_radio is null) return false;

        // Deactivating is essentially killing the one and only NAVTEX slice and leaving nothing behind.
        if (NAVTEXStatus.Active == Status)
        {
            if (_radio.SliceList.Count == 0)
            {
                // Forces the waveform to deactivate if we closed the client while it was still active but there are no slices.
                _radio.SendCommand("slice create");
                _radio.SendCommand($"slice remove 0");
            }
            else
            {
                // Get out of NT mode first to force the WF callback to return to inactive. Must be done before it is deleted.
                _radio.SendCommand($"slice s 0 mode=DIGU");
                _radio.SendCommand($"slice remove 0");
            }

            // Just so we don't create a new panadapter for the new slice each time.
            List<Panadapter> temp = _radio.PanadapterList;
            temp.ToList()?.ForEach(p => p?.Close());
            _radio.PanadapterList?.Clear();
            _radio.SendCommand($"sub slice all"); // Force GUI updates
            return true;
        }

        // Activating is a heavy-handed operation which isn't currently optimized for normal use.
        // It is anticipated that a person using their 9x00 for NAVTEX will only use one slice to transmit on that frequency.
        // If we have multiple slices open, close all but slice 0.
        // If we have no slices open, create slice 0.
        // Tune to the specified broadcast frequency, or use the international default for English-language transmission.
        if (_radio.SliceList.Count == 0)
        {
            _radio.SendCommand($"slice create");
        }
        else
        {
            for (uint i = 1; i < _radio.SliceList.Count; i++)
            {
                _radio.SendCommand($"slice remove {i}");
            }
        }
        _radio.SendCommand($"slice tune 0 {(double)broadcastFreqHz / 1000000}");
        _radio.SendCommand($"slice s 0 mode=NT");
        _radio.SendCommand($"sub slice 0"); // Force GUI update of the new slice freq
        return true;
    }

    public void SendResponseHandler(int seq, uint resp_val, string s)
    {
        // A successful response should have a response code of 0
        // and the new index should be in the string message.
        Trace.WriteLine($"Got response to 'navtex send' - seq={seq}, resp_val={resp_val}, s={s}");
        if (string.IsNullOrEmpty(s))
        {
            Trace.WriteLine($"Response message with command seq num {seq} did not include an index");
            return;
        }

        // Move the message over to the list. We can't do this before we have the index. 
        lock (_msgsLock)
        {
            if (_pendingMsgs.TryGetValue(seq, out NAVTEXMsg m))
            {
                m.Status = NAVTEXMsgStatus.Queued;
                m.Idx = uint.Parse(s);
                Msgs.Add(m);
                _pendingMsgs.Remove(seq);
                RaisePropertyChanged(nameof(Msgs));
            }
            else
            {
                // TODO raise the error to a dialog box
                Trace.WriteLine($"Failed to find a pending message with command seq num {seq}");
            }
        }
    }

    public void Send(NAVTEXMsg m)
    {
        // The serial is an optional parameter that we need not necessarily provide.
        // If they don't provide it, the waveform processor will automatically increment/roll over from the last used serial.
        string serial = m.Serial.HasValue ? $"serial_num={m.Serial.Value}" : string.Empty;

        string msg = $"navtex send tx_ident={m.TxIdent} subject_indicator={m.SubjInd} {serial} msg_text=\"{m.MsgStr}\"";
        Trace.WriteLine($"Sending NAVTEX msg - {msg}");
        lock (_msgsLock)
        {
            int seq = _radio.SendReplyCommand(new ReplyHandler(SendResponseHandler), msg);
            Trace.WriteLine($"Storing as a pending msg with seq {seq}");
            _pendingMsgs[seq] = m;
        }
    }

    private void ParseSentStatus(IEnumerable<string> s)
    {
        uint idx = 0;
        uint serial = 0;
        IEnumerable<string[]> temp = s.Where(str => !string.IsNullOrEmpty(str)).Select(strings => strings?.Split('='));
        Dictionary<string, string> kvs = temp?.ToDictionary(pair => pair[0], pair => pair[1]);

        // The incoming message status will be identified by both the index and serial.
        // The radio must provide the index since it generates them atomically.
        // The radio must also provide the effective serial in the event we did not provide one or we couldn't send with the requested one.
        // As long as we aren't doing any more sophisticated queueing, we can tie that back to the one message which was in flight.
        if (kvs.TryGetValue("idx", out string idx_str))
        {
            Trace.WriteLine($"Got idx = {idx_str}");
            if (!uint.TryParse(idx_str, out idx))
            {
                Trace.WriteLine($"Failed to parse index from {idx_str}");
                return;
            }
        }
        if (kvs.TryGetValue("serial_num", out string serial_str))
        {
            Trace.WriteLine($"Got serial = {serial_str}");
            if (!uint.TryParse(serial_str, out serial))
            {
                Trace.WriteLine($"Failed to parse serial from {serial_str}");
                return;
            }
        }

        // Redundant status? Each index is guaranteed to be unique, so we won't add duplicate entries if we see it a second time.
        if (Msgs?.Count(m => (m.Idx == idx) && m.Status == NAVTEXMsgStatus.Sent) != 0)
        {
            Trace.WriteLine($"Redundant status for index {idx}, which is already sent - discarding.");
            return;
        }

        // Use the current date+time as the sent time. This is not part of the API and is just for convenience in displaying the messages.
        string dateTime = DateTime.UtcNow.ToString("yyyy-MM-ddZHH:mm:ss");

        // Move the message from queued to sent
        lock (_msgsLock)
        {
            if (Msgs?.Count(m => m.Idx == idx) == 0)
            {
                Trace.WriteLine($"We could not find a matching message with idx {idx}! Setting as an error message.");
                Msgs.Add(new NAVTEXMsg(dateTime, idx, serial, null, null, null, NAVTEXMsgStatus.Error));
            }
            else
            {
                NAVTEXMsg msgInFlight = Msgs.First(m => (m.Idx == idx));
                if (msgInFlight.Status != NAVTEXMsgStatus.Queued)
                {
                    Trace.WriteLine("We got an update that msg idx {} was sent, but it was not queued. Setting as error message.");
                    msgInFlight.Status = NAVTEXMsgStatus.Error;
                }
                else
                {
                    msgInFlight.Status = NAVTEXMsgStatus.Sent;
                }
                msgInFlight.DateTime = dateTime;
                msgInFlight.Serial = serial;
            }
        }
        // Update view model
        RaisePropertyChanged(nameof(Msgs));
    }

    public void ParseStatus(string s)
    {
        // Sent message update
        if (s.Contains("sent"))
        {
            string[] words = s.Split(' ');
            ParseSentStatus(words.Skip(1).Take(words.Length - 1).ToArray());
            return;
        }

        // Global NAVTEX status update
        foreach (string kv in s.Split(' '))
        {
            if (kv.StartsWith("status="))
            {
                string v = kv.Split('=')[1];
                if (!Enum.TryParse(v, true, out _status))
                {
                    Trace.WriteLine($"Failed to parse status value: {v}");
                    _status = NAVTEXStatus.Error;
                }
                RaisePropertyChanged(nameof(Status));
            }
            return;
        }
    }

    public const uint INTERNATIONAL_BROADCAST_FREQ_HZ = 518000; // Does not use FEC mode.
    public const uint LOCAL_BROADCAST_FREQ_HZ = 490000; // Does not use FEC mode.
    public const uint MARINE_SAFETY_INFORMATION_BROADCAST_FREQ_HZ = 4209500; // Uses FEC mode.
}

public enum NAVTEXStatus
{
    Error, // AKA Invalid - should never be encountered
    Inactive,
    Active,
    Transmitting,
    QueueFull,
    Unlicensed,
};

public enum NAVTEXMsgStatus
{
    Error,      // Some error was encountered in processing this message
    Pending,    // Pending response from radio
    Queued,     // Radio has confirmed receipt of the message and it is queued to be sent
    Sent,       // Radio has told us that the message was transmitted
}

public class NAVTEXMsg
{
    public NAVTEXMsg(string? dateTime, uint? idx, uint? serial, char? txIdent, char? subjInd, string? msgStr, NAVTEXMsgStatus status)
    {
        DateTime = dateTime;
        Idx = idx;
        Serial = serial;
        TxIdent = txIdent;
        SubjInd = subjInd;
        MsgStr = msgStr;
        Status = status;
    }
    public string? DateTime { get; set; }
    public uint? Idx { get; set; }
    public uint? Serial { get; set; }
    public char? TxIdent { get; set; }
    public char? SubjInd { get; set; }
    public string? MsgStr { get; set; }
    public NAVTEXMsgStatus Status { get; set; }
};