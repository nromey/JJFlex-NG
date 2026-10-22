using System;
using NAudio.Wave;
using Radios;

namespace JJFlexWpf;

/// <summary>NAudio's local playback view over the shipped libFLAC decoder.</summary>
internal sealed class RecordedQsoFlacWaveStream : WaveStream
{
    private readonly RecordedQsoFlacDecoder _decoder;
    private readonly WaveFormat _format;

    internal RecordedQsoFlacWaveStream(string path)
    {
        _decoder = new RecordedQsoFlacDecoder(path);
        _format = new WaveFormat(_decoder.SampleRate, 16, _decoder.Channels);
    }

    public override WaveFormat WaveFormat => _format;
    public override long Length => _decoder.Length;
    public override long Position { get => _decoder.Position; set => _decoder.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => _decoder.Read(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _decoder.Dispose();
        base.Dispose(disposing);
    }
}
