using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>
/// The disk half of a playback take (Unified takes, design sections 1 and 4):
/// a <c>take.txt</c> in <see cref="TakeKind.AudioFormat"/> and the capture in
/// <c>heard.wav</c>, 24 kHz mono signed 16-bit, never normalized. In the style
/// of <see cref="SentAudioWriter"/>: append-only flushed facts, the file
/// finished, counted and checksummed before <c>closed</c> is written last.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a recorder.</b> It has no timer, no device and no network tap. The
/// recorder engine (<c>ComputerFlacRecorder</c>) feeds it decoded samples and
/// tells it where playback started and stopped; this class only keeps the
/// sample positions straight and the header honest.
/// </para>
/// <para>
/// <b>Never writes after <c>closed</c>.</b> Every call after <see cref="Close"/>
/// is ignored; a second Close returns the first one's result.
/// </para>
/// </remarks>
public sealed class AudioTakeWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly WavWriter _wav;
    private long _samples;
    private long? _spanStart;
    private bool _closed;
    private bool? _cleanResult;

    public string DirectoryPath { get; }
    public TakeHeader Header { get; }
    public int Rate { get; }
    public long SamplesWritten { get { lock (_gate) return _samples; } }
    public bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>
    /// A fresh folder name under <paramref name="root"/> in the shape every
    /// take folder has: the UTC creation stamp, then eight hex digits.
    /// </summary>
    public static string NewDirectory(string root) => Path.Combine(root,
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);

    public AudioTakeWriter(string directory, string kind, IEnumerable<KeyValuePair<string, string>> facts,
        int rate = AudioTakeFields.Rate)
    {
        if (!TakeKind.IsAudio(kind)) throw new ArgumentException("not-an-audio-kind", nameof(kind));
        DirectoryPath = directory;
        Rate = rate;
        Directory.CreateDirectory(directory);
        Header = new TakeHeader(Path.Combine(directory, "take.txt"), TakeKind.AudioFormat);
        try
        {
            Header.Write("kind", kind);
            Header.Write("created-utc", DateTimeOffset.UtcNow.ToString("O"));
            Header.Write(AudioTakeFields.AudioFile, AudioTakeFields.WavName);
            Header.Write(AudioTakeFields.AudioRate, rate);
            Header.Write(AudioTakeFields.AudioChannels, 1);
            Header.Write(AudioTakeFields.AudioEncoding, AudioTakeFields.Encoding);
            Header.Write(AudioTakeFields.AudioScale, "fixed-no-normalization");
            foreach (var fact in facts) Header.Write(fact.Key, fact.Value);
            _wav = new WavWriter(Path.Combine(directory, AudioTakeFields.WavName), rate, 1);
        }
        catch { Header.Dispose(); throw; }
    }

    /// <summary>Append decoded mono samples at the current position.</summary>
    public void Write(float[] mono, int count)
    {
        lock (_gate)
        {
            if (_closed || count <= 0) return;
            _wav.Write(mono, count);
            _samples += count;
        }
    }

    /// <summary>
    /// Fill a transport gap with silence and record it, so the gap stays on
    /// the timeline for listening and is left out of every measurement.
    /// </summary>
    public void Gap(long samples, string reason)
    {
        lock (_gate)
        {
            if (_closed || samples <= 0) return;
            Header.Event(AudioTakeFields.AudioGap, new AudioGapRecord(_samples, samples, reason));
            var zeros = new float[4096];
            while (samples > 0)
            {
                int n = (int)Math.Min(samples, zeros.Length);
                _wav.Write(zeros, n); _samples += n; samples -= n;
            }
        }
    }

    /// <summary>The radio reported its playback started: open a playback span here.</summary>
    public void BeginPlayback()
    {
        lock (_gate) { if (!_closed && _spanStart == null) _spanStart = _samples; }
    }

    /// <summary>The radio reported its playback stopped: close the open span here.</summary>
    public void EndPlayback()
    {
        lock (_gate)
        {
            if (_closed || _spanStart is not { } start) return;
            Header.Event(AudioTakeFields.PlaybackSpan, new AudioSpanRecord(start, _samples));
            _spanStart = null;
        }
    }

    /// <summary>Record a fact with its moment, before the take closes.</summary>
    public void Note(string key, object value)
    {
        lock (_gate) { if (!_closed) Header.Write(key, value); }
    }

    /// <summary>Record a structured event, before the take closes.</summary>
    public void NoteEvent<T>(string key, T value)
    {
        lock (_gate) { if (!_closed) Header.Event(key, value); }
    }

    /// <summary>
    /// Finish the WAV, then count and checksum it, then write the closing
    /// block with <c>closed</c> LAST. An open playback span is closed at the
    /// end of the file first. Returns whether the take closed clean.
    /// </summary>
    public bool Close(bool clean, string endReason)
    {
        lock (_gate)
        {
            if (_closed) return _cleanResult ?? false;
            if (_spanStart is { } start)
            {
                Header.Event(AudioTakeFields.PlaybackSpan, new AudioSpanRecord(start, _samples));
                _spanStart = null;
            }
            _wav.Finish();
            _wav.Dispose();
            bool ok = clean && _samples > 0;
            Header.Write(AudioTakeFields.SamplesWritten, _samples);
            Header.Write(AudioTakeFields.Checksum, TakeHeader.Checksum(Path.Combine(DirectoryPath, AudioTakeFields.WavName)));
            Header.Write(AudioTakeFields.EndReason, _samples > 0 ? endReason : "no-audio");
            Header.Write("closed", ok ? "clean" : "incomplete");
            Header.Dispose();
            _closed = true;
            _cleanResult = ok;
            return ok;
        }
    }

    /// <summary>Close as incomplete if nobody closed it.</summary>
    public void Dispose()
    {
        lock (_gate) { if (_closed) return; }
        try { Close(clean: false, "disposed-unclosed"); }
        catch (Exception) { _wav.Dispose(); Header.Dispose(); }
    }
}
