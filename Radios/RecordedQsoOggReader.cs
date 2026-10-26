using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Radios;

/// <summary>
/// Reads an Ogg Opus file (RFC 3533 and RFC 7845) into its Opus packets, each
/// with the 48 kHz sample position it starts at. The reading half of
/// <see cref="RecordedQsoOggWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why our own reader (#787, bench 2026-10-25).</b> The Opus files Save As
/// writes were handed to Windows' Media Foundation to play, and Windows has no
/// Ogg Opus decoder: the row read "length unknown" and Enter said "The byte
/// stream type is not supported." We already encode with libopus, so we decode
/// with it too, and this class does the container half.
/// </para>
/// <para>
/// The whole file is read into memory. At the 32 kbps Save As writes, an hour
/// is about 14 MB, and holding the packets makes an exact seek a lookup
/// rather than a scan.
/// </para>
/// </remarks>
public sealed class RecordedQsoOggReader
{
    /// <summary>One Opus audio packet and the 48 kHz sample it starts at.</summary>
    public readonly record struct Packet(byte[] Data, long Start, int Samples);

    public int Channels { get; }

    /// <summary>Samples at the start of the stream that are decoder priming, not audio.</summary>
    public int PreSkip { get; }

    /// <summary>The last page's granule position: where the audio ends, priming included.</summary>
    public long EndGranule { get; }

    public IReadOnlyList<Packet> Packets { get; }

    /// <summary>How many 48 kHz frames of audio the file holds, after the priming.</summary>
    public long AudioFrames => Math.Max(0, EndGranule - PreSkip);

    public RecordedQsoOggReader(string path) : this(File.ReadAllBytes(path)) { }

    public RecordedQsoOggReader(byte[] bytes)
    {
        var raw = new List<(byte[] Data, int PageIndex)>();
        var pageGranules = new List<long>();
        var partial = new MemoryStream();
        int at = 0;
        while (at + 27 <= bytes.Length)
        {
            if (bytes[at] != 'O' || bytes[at + 1] != 'g' || bytes[at + 2] != 'g' || bytes[at + 3] != 'S')
                throw new InvalidDataException("Not an Ogg file, or a damaged one: no page at byte " + at + ".");
            long granule = BitConverter.ToInt64(bytes, at + 6);
            int segments = bytes[at + 26];
            int body = at + 27 + segments;
            if (body > bytes.Length) throw new InvalidDataException("The Ogg file ends part-way through a page.");
            int page = pageGranules.Count;
            pageGranules.Add(granule);
            int cursor = body;
            for (int s = 0; s < segments; s++)
            {
                int length = bytes[at + 27 + s];
                if (cursor + length > bytes.Length) throw new InvalidDataException("The Ogg file ends part-way through a packet.");
                partial.Write(bytes, cursor, length);
                cursor += length;
                if (length < 255)
                {
                    raw.Add((partial.ToArray(), page));
                    partial.SetLength(0);
                }
            }
            at = cursor;
        }
        if (raw.Count < 2) throw new InvalidDataException("The Ogg file has no Opus header.");
        byte[] head = raw[0].Data;
        if (head.Length < 19 || Encoding.ASCII.GetString(head, 0, 8) != "OpusHead")
            throw new InvalidDataException("The Ogg file is not Opus.");
        Channels = head[9];
        if (Channels is < 1 or > 2) throw new InvalidDataException("Only mono and stereo Opus can be played here.");
        PreSkip = BitConverter.ToUInt16(head, 10);
        EndGranule = pageGranules.Count > 0 ? pageGranules[^1] : 0;

        // raw[1] is OpusTags; audio follows. Positions come from each packet's
        // own table of contents, so no packet needs decoding to be placed.
        var packets = new List<Packet>(Math.Max(0, raw.Count - 2));
        long position = 0;
        for (int i = 2; i < raw.Count; i++)
        {
            int samples = SamplesIn(raw[i].Data);
            packets.Add(new Packet(raw[i].Data, position, samples));
            position += samples;
        }

        // A stream need not start at granule zero. The first audio page's
        // granule is where its last completed packet ends, so shift every
        // start by the difference.
        if (packets.Count > 0)
        {
            int firstAudioPage = raw[2].PageIndex;
            long endOfFirstPage = 0;
            for (int i = 2; i < raw.Count && raw[i].PageIndex == firstAudioPage; i++)
                endOfFirstPage = packets[i - 2].Start + packets[i - 2].Samples;
            long shift = pageGranules[firstAudioPage] - endOfFirstPage;
            if (shift != 0)
                for (int i = 0; i < packets.Count; i++)
                    packets[i] = packets[i] with { Start = packets[i].Start + shift };
        }
        Packets = packets;
    }

    /// <summary>
    /// The number of 48 kHz samples an Opus packet decodes to, from its table
    /// of contents byte (RFC 6716 section 3.1). Zero for an empty or damaged packet.
    /// </summary>
    public static int SamplesIn(byte[] packet)
    {
        if (packet == null || packet.Length == 0) return 0;
        int config = packet[0] >> 3;
        // Frame length in units of 2.5 ms (120 samples at 48 kHz).
        int units = config < 12 ? new[] { 4, 8, 16, 24 }[config & 3]
            : config < 16 ? new[] { 4, 8 }[config & 1]
            : new[] { 1, 2, 4, 8 }[config & 3];
        int frames = (packet[0] & 3) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => packet.Length > 1 ? packet[1] & 0x3F : 0,
        };
        return frames * units * 120;
    }
}
