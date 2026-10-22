using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Radios;

/// <summary>Streams a 16-bit recorded QSO through Xiph's decoder without a Windows codec.</summary>
public sealed class RecordedQsoFlacDecoder : IDisposable
{
    private const string Library = "libFLAC.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteCallback(IntPtr decoder, IntPtr frame, IntPtr channelBuffers, IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MetadataCallback(IntPtr decoder, IntPtr metadata, IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorCallback(IntPtr decoder, int status, IntPtr client);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FLAC__stream_decoder_new();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_decoder_init_file(IntPtr decoder, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, WriteCallback write, MetadataCallback metadata, ErrorCallback error, IntPtr client);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_decoder_process_single(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_decoder_seek_absolute(IntPtr decoder, ulong sample);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_decoder_get_state(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_decoder_finish(IntPtr decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void FLAC__stream_decoder_delete(IntPtr decoder);

    private readonly WriteCallback _write;
    private readonly MetadataCallback _metadata;
    private readonly ErrorCallback _error;
    private readonly Queue<byte[]> _blocks = new();
    private byte[] _current;
    private int _currentOffset;
    private IntPtr _decoder;
    private Exception _fault;
    private long _position;
    private bool _atEnd;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalFrames { get; }
    public long Length => TotalFrames * Channels * 2;
    public long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value));
            _blocks.Clear();
            _current = null;
            _currentOffset = 0;
            if (value == Length && TotalFrames > 0)
            {
                _position = value;
                _atEnd = true;
                return;
            }
            long frame = value / (Channels * 2);
            if (FLAC__stream_decoder_seek_absolute(_decoder, (ulong)frame) == 0)
                throw new IOException("The FLAC recording could not seek to that position.");
            _position = frame * Channels * 2;
            _atEnd = false;
        }
    }

    public RecordedQsoFlacDecoder(string path)
    {
        using (var input = new BinaryReader(File.OpenRead(path)))
        {
            if (new string(input.ReadChars(4)) != "fLaC") throw new InvalidDataException("Not a FLAC file.");
            byte blockType = input.ReadByte();
            int length = (input.ReadByte() << 16) | (input.ReadByte() << 8) | input.ReadByte();
            if ((blockType & 0x7f) != 0 || length != 34) throw new InvalidDataException("FLAC stream information is missing.");
            input.ReadBytes(10);
            ulong packed = 0;
            for (int i = 0; i < 8; ++i) packed = (packed << 8) | input.ReadByte();
            SampleRate = (int)((packed >> 44) & 0xfffff);
            Channels = (int)((packed >> 41) & 7) + 1;
            int bits = (int)((packed >> 36) & 31) + 1;
            TotalFrames = (long)(packed & 0xfffffffffUL);
            if (SampleRate <= 0 || Channels is < 1 or > 2 || bits != 16)
                throw new InvalidDataException("This recording needs 16-bit mono or stereo FLAC.");
        }
        _write = Write;
        _metadata = (_, _, _) => { };
        _error = (_, status, _) => _fault = new InvalidDataException("FLAC decode error " + status + ".");
        _decoder = FLAC__stream_decoder_new();
        try
        {
            if (_decoder == IntPtr.Zero ||
                FLAC__stream_decoder_init_file(_decoder, path, _write, _metadata, _error, IntPtr.Zero) != 0)
                throw new IOException("The FLAC decoder could not open the recording.");
        }
        catch { Dispose(); throw; }
    }

    private int Write(IntPtr decoder, IntPtr frame, IntPtr channelBuffers, IntPtr client)
    {
        try
        {
            int frames = Marshal.ReadInt32(frame); // FLAC__Frame.header.blocksize is the first field.
            if (frames < 1 || frames > 65535) throw new InvalidDataException("Invalid FLAC frame size.");
            int[][] planes = new int[Channels][];
            for (int ch = 0; ch < Channels; ch++)
            {
                planes[ch] = new int[frames];
                Marshal.Copy(Marshal.ReadIntPtr(channelBuffers, ch * IntPtr.Size), planes[ch], 0, frames);
            }
            byte[] pcm = new byte[frames * Channels * 2];
            for (int i = 0, pos = 0; i < frames; ++i)
                for (int ch = 0; ch < Channels; ++ch)
                {
                    int sample = planes[ch][i];
                    pcm[pos++] = (byte)sample;
                    pcm[pos++] = (byte)(sample >> 8);
                }
            _blocks.Enqueue(pcm);
            return 0;
        }
        catch (Exception ex) { _fault = ex; return 1; }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_decoder == IntPtr.Zero) throw new ObjectDisposedException(nameof(RecordedQsoFlacDecoder));
        if (_atEnd) return 0;
        int copied = 0;
        while (count > 0)
        {
            if (_current == null || _currentOffset == _current.Length)
            {
                _current = _blocks.Count > 0 ? _blocks.Dequeue() : null;
                _currentOffset = 0;
                while (_current == null && FLAC__stream_decoder_get_state(_decoder) != 4)
                {
                    if (FLAC__stream_decoder_get_state(_decoder) > 4)
                        throw new IOException("The FLAC recording ended with a decoder error.", _fault);
                    if (FLAC__stream_decoder_process_single(_decoder) == 0 || _fault != null)
                        throw new IOException("The FLAC recording could not be decoded.", _fault);
                    _current = _blocks.Count > 0 ? _blocks.Dequeue() : null;
                }
                if (_current == null) break;
            }
            int n = Math.Min(count, _current.Length - _currentOffset);
            Array.Copy(_current, _currentOffset, buffer, offset, n);
            _currentOffset += n;
            offset += n;
            count -= n;
            copied += n;
            _position += n;
        }
        return copied;
    }

    public void Dispose()
    {
        if (_decoder == IntPtr.Zero) return;
        FLAC__stream_decoder_finish(_decoder);
        FLAC__stream_decoder_delete(_decoder);
        _decoder = IntPtr.Zero;
    }
}
