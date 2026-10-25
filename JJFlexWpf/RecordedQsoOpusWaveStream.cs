using System;
using NAudio.Wave;
using POpusCodec;
using POpusCodec.Enums;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Plays an Ogg Opus file through libopus, the same library Save As encodes
/// it with (#787). Windows has no Ogg Opus decoder, so Media Foundation
/// refused these files outright ("The byte stream type is not supported").
/// </summary>
/// <remarks>
/// <para>
/// 48 kHz float, mono or stereo as the file says. The decoder's priming
/// (the header's pre-skip) is dropped from the front and the encoder's
/// padding from the end, so the length is the length that was saved.
/// </para>
/// <para>
/// <b>Seeking</b> restarts the decoder a little before the target and throws
/// away what it decodes up to the target: Opus needs some audio behind a
/// point to rebuild it cleanly (RFC 7845 recommends 80 ms).
/// </para>
/// </remarks>
public sealed class RecordedQsoOpusWaveStream : WaveStream
{
    private const int Rate = 48000;
    private const int PreRollSamples = 3840; // 80 ms at 48 kHz

    private readonly RecordedQsoOggReader _ogg;
    private readonly WaveFormat _format;
    private readonly int _channels;
    private OpusDecoder _decoder;
    private int _next;              // the next packet to decode
    private float[] _pending = Array.Empty<float>();
    private int _pendingAt;         // floats of _pending already handed out
    private long _discard;          // samples still to throw away before audio is handed out
    private long _frame;            // the output frame position, after the pre-skip

    public RecordedQsoOpusWaveStream(string path) : this(new RecordedQsoOggReader(path)) { }

    internal RecordedQsoOpusWaveStream(RecordedQsoOggReader ogg)
    {
        _ogg = ogg;
        _channels = ogg.Channels;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, _channels);
        _decoder = NewDecoder();
        StartAt(0);
    }

    public override WaveFormat WaveFormat => _format;

    public override long Length => _ogg.AudioFrames * _format.BlockAlign;

    public override long Position
    {
        get => _frame * _format.BlockAlign;
        set => StartAt(Math.Clamp(value / _format.BlockAlign, 0, _ogg.AudioFrames));
    }

    private OpusDecoder NewDecoder() =>
        new(SamplingRate.Sampling48000, _channels == 1 ? Channels.Mono : Channels.Stereo);

    /// <summary>Restart decoding so the next audio handed out is output frame <paramref name="frame"/>.</summary>
    private void StartAt(long frame)
    {
        long target = frame + _ogg.PreSkip;           // in stream samples
        long from = Math.Max(0, target - PreRollSamples);
        int index = 0;
        var packets = _ogg.Packets;
        while (index + 1 < packets.Count && packets[index + 1].Start <= from) index++;
        _decoder.Dispose();
        _decoder = NewDecoder();
        _next = index;
        _pending = Array.Empty<float>();
        _pendingAt = 0;
        long firstStart = packets.Count > 0 ? packets[index].Start : 0;
        _discard = Math.Max(0, target - firstStart);
        _frame = frame;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int block = _format.BlockAlign;
        long remainingFrames = _ogg.AudioFrames - _frame;
        int wantFrames = (int)Math.Min(count / block, Math.Max(0, remainingFrames));
        int written = 0;
        while (written < wantFrames)
        {
            if (_pendingAt >= _pending.Length && !DecodeNext()) break;
            int availableFrames = (_pending.Length - _pendingAt) / _channels;
            if (_discard > 0)
            {
                int drop = (int)Math.Min(_discard, availableFrames);
                _pendingAt += drop * _channels;
                _discard -= drop;
                continue;
            }
            int take = Math.Min(availableFrames, wantFrames - written);
            Buffer.BlockCopy(_pending, _pendingAt * sizeof(float), buffer, offset + written * block, take * block);
            _pendingAt += take * _channels;
            written += take;
        }
        _frame += written;
        return written * block;
    }

    private bool DecodeNext()
    {
        if (_next >= _ogg.Packets.Count) return false;
        _pending = _decoder.DecodePacketFloat(_ogg.Packets[_next++].Data);
        _pendingAt = 0;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _decoder.Dispose();
        base.Dispose(disposing);
    }
}
