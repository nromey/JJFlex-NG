using System;
using System.IO;
using NAudio.Wave;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Opens the files Save As writes into Recorded QSOs (#787) that are not the
/// recorder's FLAC: WAV through NAudio's reader, and MP3, Opus and M4A through
/// Windows' own decoders. Noel, 2026-10-25: those files belong in the list "so
/// we can delete them and prune", so the list needs their lengths and the
/// player needs to open them.
/// </summary>
/// <remarks>
/// Windows decides what it can decode. When it cannot (an N edition without
/// the Media Feature Pack, or no Opus decoder), the catalog lists the file
/// with its length unknown and the player refuses with Windows' reason; the
/// file can still be deleted.
/// </remarks>
public static class RecordedQsoMedia
{
    /// <summary>Install <see cref="ReadInfo"/> as the catalog's reader. Called once at startup.</summary>
    public static void Install() => RecordedQsoCatalog.OtherReader = ReadInfo;

    /// <summary>Open a WAV, MP3, Opus or M4A file for playback.</summary>
    public static WaveStream Open(string path) =>
        Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);

    /// <summary>The file's rate, channels and length in frames. Throws when it cannot be read.</summary>
    public static (int Rate, int Channels, long Frames) ReadInfo(string path)
    {
        using var reader = Open(path);
        var format = reader.WaveFormat;
        long frames = (long)Math.Round(reader.TotalTime.TotalSeconds * format.SampleRate);
        return (format.SampleRate, format.Channels, frames);
    }
}
