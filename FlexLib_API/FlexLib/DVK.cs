using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using Flex.UiWpfFramework.Mvvm;
namespace Flex.Smoothlake.FlexLib;

public class DVK : ObservableObject
{
    private Radio _radio;

    public DVK(Radio radio)
    {
        _radio = radio;
        _status = new DVKStatus(DVKStatusType.Unknown, null);
    }

    public void SendCommand(DVKCommand cmd)
    {
        _radio.SendCommand($"{cmd}");
    }

    public void ShowWarning(MessageSeverity severity, string message)
    {
        _radio.OnMessageReceived(severity, message);
    }

    public void ParseStatus(string s)
    {
        if (s.Contains("status=")) // Global DVK status for all recordings
        {
            Status = new DVKStatus(s.Split(' '));
        }
        else // Specific recording has been added/deleted/modified
        {
            var status = new DVKRecordingStatus(s);
            if (status.Added)
            {
                DVKRecording temp = _recordings.FirstOrDefault(rec => rec.Id == status.Id);
                if (null != temp)
                {
                    _recordings.Remove(temp);
                }
                var recording = new DVKRecording(status.Id, status.Name ?? string.Empty, status.Duration ?? 0);
                _recordings.Add(recording);
            }
            else if (status.Deleted)
            {
                DVKRecording temp = _recordings.FirstOrDefault(rec => rec.Id == status.Id);
                if (null != temp)
                {
                    _recordings.Remove(temp);
                }
                else
                {
                    Trace.WriteLine($"Failed to delete DVK recording in id {status.Id}");
                }
            }
            RaisePropertyChanged(nameof(Recordings));
        }
    }

    private DVKStatus _status;
    public DVKStatus Status
    {
        get => _status;
        set
        {
            if (value == _status) return;
            _status = value;
            RaisePropertyChanged(nameof(Status));
        }
    }

    private List<DVKRecording> _recordings = [];
    public List<DVKRecording> Recordings
    {
        get => _recordings;
        set
        {
            if (value == _recordings) return;
            _recordings = value;
            RaisePropertyChanged(nameof(Recordings));
        }
    }

    public async Task<string> DownloadWAVFile(string downloadPath, uint id, string name)
    {
        Trace.WriteLine($"Downloading DVK recording WAV file at index {id}");
        string portString;

        try
        {
            portString = await _radio.SendCommandAsync(new DVKCommand(DVKCommandType.DownloadRecording, id, null).ToString());
        }
        catch (SmartSdrCommandErrorException ex)
        {
            Trace.WriteLine($"Failed to execute dvk download Command: {ex}");
            return null;
        }

        if (!int.TryParse(portString, out int port))
        {
            Trace.WriteLine($"Invalid Port Number: {portString}");
            return null;
        }

        // Use command response to establish TCP connection on radio's file server.
        var server = new TcpListener(IPAddress.Any, port);
        server.Start();
        Trace.WriteLine("Waiting for the DVK download connection from the radio");
        using TcpClient client = await server.AcceptTcpClientAsync();
        Trace.WriteLine("Got a TCP connection from the radio");
        using NetworkStream stream = client.GetStream();

        // Download to the specified file name, or use default name if not provided.
        Debug.WriteLine($"Downloading DVK recording index {id}");
        Directory.CreateDirectory(downloadPath);
        string defaultName = $"Recording_{id}";
        string filePath = $@"{downloadPath}\{(name is null ? defaultName : name)}.wav";
        using FileStream outputFile = File.Open(filePath, FileMode.Create);
        await stream.CopyToAsync(outputFile);

        Debug.WriteLine($"Download of index {id} complete");
        server.Stop();
        return filePath;
    }

    public const uint MAX_WAV_FILE_SIZE_BYTES = 5000000; // 5MB is well over 10 sec of audio at the supported sample rate.
}

#region Command and Status Parsing Helpers

public class DVKStatus
{
    public DVKStatusType Type;
    public uint? Id;
    public DVKStatus(IEnumerable<string> s)
    {
        IEnumerable<string[]> temp = s.Where(str => !string.IsNullOrEmpty(str)).Select(strings => strings?.Split('='));
        Dictionary<string, string> kvs = temp?.ToDictionary(pair => pair[0], pair => pair[1]);
        if (kvs.TryGetValue("status", out string status))
        {
            Type = _statuses[status];
        }
        if (kvs.TryGetValue("id", out string id))
        {
            if (uint.TryParse(id, out uint parsedId))
            {
                Id = parsedId;
            }
        }
        // If disabled, ignore the status and clear the id because the client must disregard it.
        if (kvs.TryGetValue("enabled", out string enabledStr))
        {
            if (uint.TryParse(enabledStr, out uint enabled) && enabled == 0)
            {
                Type = DVKStatusType.Disabled;
                Id = null;
            }
        }
    }

    public DVKStatus(DVKStatusType type, uint? id)
    {
        Type = type;
        Id = id;
    }

    public override string ToString()
    {
        string baseStatus = _statuses.FirstOrDefault(s => s.Value == Type).Key;
        if (baseStatus is null) return "Initializing..."; // Assume if we have an unknown status, we just haven't received any DVK statuses yet.
        return  baseStatus[0].ToString().ToUpper() + baseStatus.Substring(1);
    }

    private static readonly Dictionary<string, DVKStatusType> _statuses = new()
    {
        { "disabled", DVKStatusType.Disabled },
        { "idle", DVKStatusType.Idle },
        { "recording", DVKStatusType.Recording },
        { "preview", DVKStatusType.Preview },
        { "playback", DVKStatusType.Playback },
    };
}

public class DVKRecordingStatus
{
    public bool Added;
    public bool Deleted;
    public uint Id;
    public string Name;
    public uint? Duration;

    public DVKRecordingStatus(string str)
    {
        IEnumerable<string> s = str.Split(' ');
        if (s.Contains("deleted"))
        {
            Deleted = true;
            s = s.Skip(1).ToArray();
        }
        else if (s.Contains("added"))
        {
            Added = true;
            s = s.Skip(1).ToArray();
        }
        else
        {
            Added = true;
        }

        string name = string.Empty;
        if (Added)
        {
            // Get name first, since it is enclosed by quotes and may contain spaces.
            // It will complicate parsing after splitting by = and space.
            int nameStart = str.IndexOf("\"");
            int nameEnd = str.IndexOf("\"", nameStart + 1);
            name = str.Substring(nameStart + 1, nameEnd - nameStart - 1);
        }

        // Split by spaces to more easily parse out key-value pairs.
        IEnumerable<string[]> temp = s.Where(str => !string.IsNullOrEmpty(str)).Select(strings => strings?.Split('='));
        Dictionary<string, string> kvs = temp?.Where(kv => kv.Length > 1)?.ToDictionary(pair => pair[0], pair => pair[1]);
        if (kvs.TryGetValue("id", out string idStr))
        {
            Id = uint.Parse(idStr);
        }
        if (Added)
        {
            Name = name;
            if (kvs.TryGetValue("duration", out string durationStr))
            {
                if (uint.TryParse(durationStr, out uint durationParsed))
                {
                    Duration = durationParsed;
                }
            }
        }
    }
}

public class DVKCommand
{
    private uint? _id;
    private string _name;
    private DVKCommandType _type;
    public DVKCommand(DVKCommandType type, uint? id, string name)
    {
        _type = type;
        _id = id;
        _name = name;
    }

    public override string ToString()
    {
        string nameParam = _name is null ? string.Empty : $" name=\"{_name}\"";
        string idParam = _id is null ? string.Empty : $" id={_id}";
        return $"dvk {(_verbs[_type])}{nameParam}{idParam}";
    }

    private static readonly Dictionary<DVKCommandType, string> _verbs = new()
    {
        { DVKCommandType.Create, "create" },
        { DVKCommandType.StartRecording, "rec_start" },
        { DVKCommandType.StopRecording, "rec_stop" },
        { DVKCommandType.DeleteRecording, "remove" },
        { DVKCommandType.StartPreview, "preview_start" },
        { DVKCommandType.StopPreview, "preview_stop" },
        { DVKCommandType.StartPlayback, "playback_start" },
        { DVKCommandType.StopPlayback, "playback_stop" },
        { DVKCommandType.SetName, "set_name" },
        { DVKCommandType.ClearRecording, "clear" },
        { DVKCommandType.DownloadRecording, "download" },
        { DVKCommandType.UploadRecording, "upload" },
    };
}

public enum DVKCommandType
{
    Create,
    StartRecording,
    StopRecording,
    DeleteRecording,
    StartPreview,
    StopPreview,
    StartPlayback,
    StopPlayback,
    SetName,
    ClearRecording,
    DownloadRecording,
    UploadRecording,
};

public enum DVKStatusType
{
    Unknown,
    Disabled,
    Idle,
    Recording,
    Preview,
    Playback,
};

#endregion

public class DVKRecordingComparer : System.Collections.IComparer
{
    public int Compare(object x, object y)
    {
        var obj1 = x as DVKRecording;
        var obj2 = y as DVKRecording;
        if (obj1 == null || obj2 == null) return 0;
        return obj1.Id.CompareTo(obj2.Id);
    }
}

public class DVKRecording : ObservableObject
{
    private uint _id;
    public uint Id
    {
        get => _id;
        set
        {
            if (value == _id) return;
            _id = value;
            Debug.WriteLine("Changed ID of voice recording!");
            RaisePropertyChanged(nameof(Id));
        }
    }

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            RaisePropertyChanged(nameof(Name));
        }
    }

    private uint _durationMilliseconds;
    public uint DurationMilliseconds
    {
        get => _durationMilliseconds;
        set
        {
            if (value == _durationMilliseconds) return;
            _durationMilliseconds = value;
            RaisePropertyChanged(nameof(DurationMilliseconds));
        }
    }

    public DVKRecording(uint id, string name, uint durationMilliseconds)
    {
        Id = id;
        Name = name;
        DurationMilliseconds = durationMilliseconds;
    }

    public static readonly char[] FORBIDDEN_NAME_CHARS = { '\'', '\"' }; // The name will be sent back over the radio API and parsed as a single string in quotes,
                                                                         // so prevent user from entering quotes in the name itself.
}
