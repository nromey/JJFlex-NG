using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Radios;

/// <summary>Writes an Ogg Opus stream (RFC 3533 and RFC 7845) without another native codec.</summary>
public sealed class RecordedQsoOggWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly uint _serial = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly List<byte[]> _packets = new();
    private uint _pageNumber;
    private long _encodedFrames;
    private bool _finished;

    public RecordedQsoOggWriter(string path, int channels, int preSkip)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var head = new MemoryStream();
        using (var w = new BinaryWriter(head, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("OpusHead"));
            w.Write((byte)1);
            w.Write((byte)channels);
            w.Write((ushort)preSkip);
            w.Write(48000u);
            w.Write((short)0);
            w.Write((byte)0);
        }
        WritePage(new[] { head.ToArray() }, 0, 2);
        byte[] vendor = Encoding.UTF8.GetBytes("JJ Flexible Radio");
        using var tags = new MemoryStream();
        using (var w = new BinaryWriter(tags, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("OpusTags"));
            w.Write((uint)vendor.Length);
            w.Write(vendor);
            w.Write(0u);
        }
        WritePage(new[] { tags.ToArray() }, 0, 0);
    }

    public void AddAudioPacket(byte[] packet, int frames)
    {
        if (_finished) throw new InvalidOperationException("The Ogg recording is complete.");
        if (packet == null || packet.Length == 0) throw new InvalidDataException("The Opus encoder returned an empty packet.");
        if (_packets.Count >= 25) FlushAudio(final: false, _encodedFrames);
        _packets.Add(packet);
        _encodedFrames += frames;
    }

    public void Finish(long finalGranule)
    {
        if (_finished) return;
        if (finalGranule > _encodedFrames) throw new InvalidDataException("The Opus encoder did not cover its lookahead.");
        FlushAudio(final: true, finalGranule);
        _file.Flush(flushToDisk: true);
        _finished = true;
    }

    private void FlushAudio(bool final, long granule)
    {
        if (_packets.Count == 0) return;
        WritePage(_packets, granule, final ? (byte)4 : (byte)0);
        _packets.Clear();
    }

    private void WritePage(IEnumerable<byte[]> packets, long granule, byte flags)
    {
        var segments = new List<byte>();
        using var body = new MemoryStream();
        foreach (byte[] packet in packets)
        {
            int remaining = packet.Length;
            while (remaining >= 255) { segments.Add(255); remaining -= 255; }
            segments.Add((byte)remaining);
            body.Write(packet);
        }
        if (segments.Count > 255) throw new InvalidDataException("Too many Ogg segments in one page.");
        byte[] page = new byte[27 + segments.Count + body.Length];
        Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[4] = 0;
        page[5] = flags;
        BitConverter.GetBytes(granule).CopyTo(page, 6);
        BitConverter.GetBytes(_serial).CopyTo(page, 14);
        BitConverter.GetBytes(_pageNumber++).CopyTo(page, 18);
        page[26] = (byte)segments.Count;
        segments.CopyTo(page, 27);
        body.ToArray().CopyTo(page, 27 + segments.Count);
        uint crc = 0;
        foreach (byte value in page)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        BitConverter.GetBytes(crc).CopyTo(page, 22);
        _file.Write(page);
    }

    public void Dispose()
    {
        if (!_finished) _file.Flush(flushToDisk: true);
        _file.Dispose();
    }
}
