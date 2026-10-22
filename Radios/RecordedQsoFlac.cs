using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Radios;

/// <summary>One FLAC file, owned by the recorder's writer thread.</summary>
public sealed class RecordedQsoFlac : IDisposable
{
    private const string Library = "libFLAC.dll";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteCallback(IntPtr encoder, IntPtr buffer, nuint bytes, uint samples, uint frame, IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekCallback(IntPtr encoder, ulong absolute, IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TellCallback(IntPtr encoder, out ulong absolute, IntPtr client);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FLAC__stream_encoder_new();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_set_channels(IntPtr e, uint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_set_bits_per_sample(IntPtr e, uint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_set_sample_rate(IntPtr e, uint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_set_compression_level(IntPtr e, uint value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_init_stream(IntPtr e, WriteCallback write, SeekCallback seek, TellCallback tell, IntPtr metadata, IntPtr client);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_process_interleaved(IntPtr e, int[] samples, uint frames);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int FLAC__stream_encoder_finish(IntPtr e);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void FLAC__stream_encoder_delete(IntPtr e);

    private readonly FileStream _file;
    private readonly WriteCallback _write;
    private readonly SeekCallback _seek;
    private readonly TellCallback _tell;
    private readonly int _channels;
    private IntPtr _encoder;
    private Exception _writeFailure;

    public RecordedQsoFlac(string path, int sampleRate, int channels)
    {
        _channels = channels;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        _write = Write;
        _seek = Seek;
        _tell = Tell;
        try
        {
            _encoder = FLAC__stream_encoder_new();
            if (_encoder == IntPtr.Zero ||
                FLAC__stream_encoder_set_channels(_encoder, (uint)channels) == 0 ||
                FLAC__stream_encoder_set_bits_per_sample(_encoder, 16) == 0 ||
                FLAC__stream_encoder_set_sample_rate(_encoder, (uint)sampleRate) == 0 ||
                FLAC__stream_encoder_set_compression_level(_encoder, 5) == 0 ||
                FLAC__stream_encoder_init_stream(_encoder, _write, _seek, _tell, IntPtr.Zero, IntPtr.Zero) != 0)
                throw new IOException("The FLAC encoder could not open the recording.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private int Write(IntPtr encoder, IntPtr buffer, nuint bytes, uint samples, uint frame, IntPtr client)
    {
        try
        {
            int count = checked((int)bytes);
            byte[] copy = new byte[count];
            Marshal.Copy(buffer, copy, 0, count);
            _file.Write(copy);
            _file.Flush(flushToDisk: true);
            return 0;
        }
        catch (Exception ex) { _writeFailure = ex; return 1; }
    }

    private int Seek(IntPtr encoder, ulong absolute, IntPtr client)
    {
        try { _file.Position = checked((long)absolute); return 0; }
        catch (Exception ex) { _writeFailure = ex; return 1; }
    }

    private int Tell(IntPtr encoder, out ulong absolute, IntPtr client)
    {
        try { absolute = (ulong)_file.Position; return 0; }
        catch (Exception ex) { absolute = 0; _writeFailure = ex; return 1; }
    }

    public void Write(float[] interleaved)
    {
        if (interleaved.Length % _channels != 0) throw new ArgumentException("Incomplete audio frame.");
        int[] pcm = new int[interleaved.Length];
        for (int i = 0; i < pcm.Length; ++i)
        {
            float sample = interleaved[i];
            if (float.IsNaN(sample)) sample = 0;
            pcm[i] = (int)(Math.Clamp(sample, -1f, 1f) * 32767f);
        }
        if (FLAC__stream_encoder_process_interleaved(_encoder, pcm, (uint)(pcm.Length / _channels)) == 0)
            throw new IOException("The FLAC recording stopped because a write failed.", _writeFailure);
    }

    public void Dispose()
    {
        Exception finishFailure = null;
        if (_encoder != IntPtr.Zero)
        {
            try
            {
                if (FLAC__stream_encoder_finish(_encoder) == 0)
                    finishFailure = new IOException("The FLAC recording could not be finalized.", _writeFailure);
            }
            finally { FLAC__stream_encoder_delete(_encoder); _encoder = IntPtr.Zero; }
        }
        _file.Dispose();
        if (finishFailure != null) throw finishFailure;
    }
}
