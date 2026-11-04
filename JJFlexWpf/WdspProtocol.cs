using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace JJFlexWpf
{
    // Contract 2, protocol version 1: the WDSP helper's pipe protocol, as pure
    // code. No I/O lives here. Every layout below is the wire appendix's, field
    // by field; where this file and the appendix disagree, the appendix wins.
    // This file is MIT and is written from the contract alone. It is linked by
    // the MIT test stub that plays the helper, so it encodes both directions.

    /// <summary>Message type numbers (u16 on the wire).</summary>
    internal enum WdspMessageType : ushort
    {
        Invalid = 0x0000,
        Hello = 0x0001,
        HelloAck = 0x0002,
        HelloReject = 0x0003,
        OpenChannel = 0x0010,
        OpenChannelAck = 0x0011,
        CloseChannel = 0x0012,
        CloseChannelAck = 0x0013,
        Frame = 0x0020,
        FrameOut = 0x0021,
        SetSetting = 0x0030,
        SettingAck = 0x0031,
        /// <summary>Reserved: was LatencyChanged. Never sent; skipped as unknown.</summary>
        ReservedLatencyChanged = 0x0032,
        Heartbeat = 0x0040,
        Error = 0x0050,
    }

    /// <summary>The one u16 result and error code space.</summary>
    internal enum WdspResult : ushort
    {
        Ok = 0,
        UnknownChannel = 1,
        ChannelIdInUse = 2,
        EngineNotOffered = 3,
        DirectionNotServed = 4,
        UnknownSetting = 5,
        InvalidValue = 6,
        SettingNeedsNewChannel = 7,
        WrongFrameLength = 8,
        SeqNotIncreasing = 9,
        InvalidSample = 10,
        TooManyChannels = 11,
        ChannelBuildFailed = 12,
        Internal = 13,
        MalformedMessage = 256,
        UnexpectedMessage = 257,
    }

    /// <summary>Which end is reading: decides which known types are from the wrong sender.</summary>
    internal enum WdspRole { JJFlexible, Helper }

    /// <summary>
    /// Conditions that are not result codes but still decide an engine's
    /// <see cref="Unavailability"/>; see <see cref="WdspProtocol.MapCondition"/>.
    /// </summary>
    internal enum WdspCondition
    {
        HelloReject, InvalidHelloAck, VersionNotSpoken, EngineNotOffered, DirectionNotServed,
        WrongSampleRate, WrongFrameLength, ReopenLatencyDiffers,
        Stall, BrokenPipe, ProcessExited, FatalProtocolError, HandshakeDeadline, StartFailed,
        FileMissing, StartBlocked, SettingRefused, NonFatalError,
    }

    /// <summary>A protocol error: fatal, the connection is broken.</summary>
    internal sealed class WdspProtocolException : Exception
    {
        public WdspResult Code { get; }
        public WdspProtocolException(string message, WdspResult code = WdspResult.MalformedMessage) : base(message) => Code = code;
    }

    /// <summary>A value: kind 0 number, 1 choice, 255 none.</summary>
    internal readonly record struct WdspValue(byte Kind, double Number, string? Choice)
    {
        public const byte NumberKind = 0, ChoiceKind = 1, NoneKind = 255;
        public static WdspValue None => new(NoneKind, 0, null);
        public static WdspValue From(EngineSettingValue value) =>
            value.Choice != null ? new(ChoiceKind, 0, value.Choice) : new(NumberKind, value.Number, null);
        public EngineSettingValue ToSetting() => Kind == ChoiceKind ? new EngineSettingValue(0, Choice) : new EngineSettingValue(Number);
        /// <summary>Exact equality: bit for bit for a number, byte for byte for a choice.</summary>
        public bool SameAs(WdspValue other) => Kind == other.Kind &&
            (Kind == ChoiceKind ? string.Equals(Choice, other.Choice, StringComparison.Ordinal)
                : BitConverter.DoubleToInt64Bits(Number) == BitConverter.DoubleToInt64Bits(other.Number));
    }

    internal sealed record WdspNamedValue(string Name, WdspValue Value);

    internal sealed record WdspHello(ushort Lowest, ushort Highest, string JJFlexibleVersion);
    internal sealed record WdspHelloReject(ushort Lowest, ushort Highest, string Detail);
    internal sealed record WdspFingerprint(string EngineId, byte Slot, string Option, byte[] Sha256);
    internal sealed record WdspEngineOffer(string Id, byte Directions, uint SampleRate, ushort FrameLength,
        uint LatencySamples, ushort CalibrationFrames, uint MedianNanos, uint P99Nanos,
        IReadOnlyList<EngineSettingDescriptor> Settings);
    internal sealed record WdspHelloAck(ushort Version, string HelperVersion, string WdspVersion, string WdspCommit,
        string FftwVersion, ushort MaxChannels, IReadOnlyList<WdspFingerprint> Fingerprints,
        IReadOnlyList<WdspEngineOffer> Engines);
    internal sealed record WdspOpenChannel(uint Channel, byte Direction, string EngineId, IReadOnlyList<WdspNamedValue> Initial);
    internal sealed record WdspOpenChannelAck(uint Channel, WdspResult Result, uint LatencySamples,
        IReadOnlyList<WdspNamedValue> Applied, string Detail);
    internal sealed record WdspCloseChannel(uint Channel);
    internal sealed record WdspCloseChannelAck(uint Channel, WdspResult Result);
    internal sealed record WdspFrame(uint Channel, ulong Seq, float[] Samples);
    internal sealed record WdspFrameOut(uint Channel, ulong Seq, uint ProcessingNanos, float[] Samples);
    internal sealed record WdspSetSetting(uint Channel, WdspNamedValue Setting);
    internal sealed record WdspSettingAck(uint Channel, WdspResult Result, string Name, WdspValue Value);
    internal sealed record WdspHeartbeat
    {
        public static readonly WdspHeartbeat Instance = new();
    }
    internal sealed record WdspError(uint Channel, WdspResult Code, string Text);

    /// <summary>
    /// Accumulates bytes from the pipe into whole messages. A partial read is
    /// never a message. The length is checked (2 to 65,536) before anything is
    /// stored for the message, and nothing is ever allocated per message: the
    /// one buffer is made at construction. Unknown types are skipped by their
    /// length and counted; the caller traces them.
    /// </summary>
    internal sealed class WdspFrameReader
    {
        private readonly byte[] _buffer = new byte[4 + WdspProtocol.MaxLength];
        private int _have;
        private int _need = 4;

        public bool HasMessage { get; private set; }
        public bool Failed { get; private set; }
        public uint FailedLength { get; private set; }
        public WdspMessageType Type { get; private set; }
        public ReadOnlySpan<byte> Payload => HasMessage ? _buffer.AsSpan(6, _need - 6) : default;
        public long SkippedCount { get; private set; }
        public ushort LastSkippedType { get; private set; }
        public int LastSkippedLength { get; private set; }

        /// <summary>
        /// Take bytes for the message in progress. Returns how many were
        /// consumed; stops at the end of a message (then <see cref="HasMessage"/>
        /// is true until <see cref="Release"/>) or at a refused length (then
        /// <see cref="Failed"/> is true for good).
        /// </summary>
        public int Push(ReadOnlySpan<byte> data)
        {
            if (HasMessage) throw new InvalidOperationException("Release the current message first.");
            int consumed = 0;
            while (!Failed && !HasMessage && consumed < data.Length)
            {
                int take = Math.Min(_need - _have, data.Length - consumed);
                data.Slice(consumed, take).CopyTo(_buffer.AsSpan(_have));
                _have += take;
                consumed += take;
                if (_have < _need) break;
                if (_need == 4)
                {
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
                    if (length < WdspProtocol.MinLength || length > WdspProtocol.MaxLength)
                    {
                        Failed = true;
                        FailedLength = length;
                        break;
                    }
                    _need = 4 + (int)length;
                    continue;
                }
                var type = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(4));
                if (WdspProtocol.IsAssigned(type))
                {
                    Type = (WdspMessageType)type;
                    HasMessage = true;
                }
                else
                {
                    SkippedCount++;
                    LastSkippedType = type;
                    LastSkippedLength = _need - 4;
                    _have = 0; _need = 4;
                }
            }
            return consumed;
        }

        /// <summary>Done with the current message; the next one may begin.</summary>
        public void Release()
        {
            HasMessage = false;
            _have = 0;
            _need = 4;
        }
    }

    /// <summary>Encoding, decoding, validation and the mapping onto <see cref="Unavailability"/>.</summary>
    internal static class WdspProtocol
    {
        public const ushort ProtocolVersion = 1;
        public const int MinLength = 2;
        public const int MaxLength = 65536;
        public const int MaxString = 4096;
        public const int SampleRate = 48000;
        public const int FrameLength = 480;
        public const byte Receive = 0, Transmit = 1;

        private static readonly UTF8Encoding Strict = new(false, true);

        public static bool IsAssigned(ushort type) => type switch
        {
            0x0000 or 0x0001 or 0x0002 or 0x0003 or 0x0010 or 0x0011 or 0x0012 or 0x0013 or
            0x0020 or 0x0021 or 0x0030 or 0x0031 or 0x0040 or 0x0050 => true,
            _ => false,
        };

        /// <summary>Who may send a known type: true when <paramref name="sender"/> may.</summary>
        public static bool SentBy(WdspMessageType type, WdspRole sender) => type switch
        {
            WdspMessageType.Hello or WdspMessageType.OpenChannel or WdspMessageType.CloseChannel or
            WdspMessageType.Frame or WdspMessageType.SetSetting => sender == WdspRole.JJFlexible,
            WdspMessageType.HelloAck or WdspMessageType.HelloReject or WdspMessageType.OpenChannelAck or
            WdspMessageType.CloseChannelAck or WdspMessageType.FrameOut or WdspMessageType.SettingAck => sender == WdspRole.Helper,
            WdspMessageType.Heartbeat or WdspMessageType.Error => true,
            _ => false,
        };

        // ------------------------------------------------------------------
        // String rules
        // ------------------------------------------------------------------

        /// <summary>Engine ids, setting names, option names: 1 to 64 lower-case letters, digits, underscore, starting with a letter.</summary>
        public static bool IsName(string s)
        {
            if (s.Length < 1 || s.Length > 64 || s[0] < 'a' || s[0] > 'z') return false;
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        /// <summary>Label and unit keys: 1 to 128 lower-case letters, digits, underscore and full stop.</summary>
        public static bool IsKey(string s)
        {
            if (s.Length < 1 || s.Length > 128) return false;
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.')) return false;
            return true;
        }

        /// <summary>Version strings: 1 to 128 bytes of printable ASCII.</summary>
        public static bool IsVersion(string s)
        {
            if (s.Length < 1 || s.Length > 128) return false;
            foreach (char c in s) if (c < 32 || c > 126) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // Encoding
        // ------------------------------------------------------------------

        private sealed class Writer
        {
            private byte[] _b = new byte[256];
            public int Length;
            private void Need(int n) { if (Length + n > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, Length + n)); }
            public void U8(byte v) { Need(1); _b[Length++] = v; }
            public void U16(ushort v) { Need(2); BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(Length), v); Length += 2; }
            public void U32(uint v) { Need(4); BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(Length), v); Length += 4; }
            public void U64(ulong v) { Need(8); BinaryPrimitives.WriteUInt64LittleEndian(_b.AsSpan(Length), v); Length += 8; }
            public void F64(double v) { Need(8); BinaryPrimitives.WriteDoubleLittleEndian(_b.AsSpan(Length), v); Length += 8; }
            public void F32(float v) { Need(4); BinaryPrimitives.WriteSingleLittleEndian(_b.AsSpan(Length), v); Length += 4; }
            public void Bytes(ReadOnlySpan<byte> v) { Need(v.Length); v.CopyTo(_b.AsSpan(Length)); Length += v.Length; }
            public void Str(string s)
            {
                int n = Strict.GetByteCount(s);
                if (n > MaxString) throw new ArgumentException("A string longer than 4,096 bytes cannot be sent.");
                U16((ushort)n);
                Need(n);
                Length += Strict.GetBytes(s, 0, s.Length, _b, Length);
            }
            public void Count8(int count, int max, string what)
            {
                if (count < 0 || count > max) throw new ArgumentException($"{what} count {count} is outside 0 to {max}.");
                U8((byte)count);
            }
            public byte[] Finish(WdspMessageType type)
            {
                int length = Length + 2;
                if (length > MaxLength) throw new ArgumentException("Message longer than the 65,536-byte cap.");
                var message = new byte[4 + length];
                BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)length);
                BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), (ushort)type);
                _b.AsSpan(0, Length).CopyTo(message.AsSpan(6));
                return message;
            }
        }

        private static void WriteValue(Writer w, WdspValue v)
        {
            w.U8(v.Kind);
            if (v.Kind == WdspValue.NumberKind) w.F64(v.Number);
            else if (v.Kind == WdspValue.ChoiceKind) w.Str(v.Choice ?? "");
            else if (v.Kind != WdspValue.NoneKind) throw new ArgumentException("Unknown value kind " + v.Kind);
        }

        private static void WriteNamed(Writer w, WdspNamedValue v) { w.Str(v.Name); WriteValue(w, v.Value); }

        private static void WriteDescriptor(Writer w, EngineSettingDescriptor d)
        {
            w.Str(d.Name);
            w.Str(d.LabelKey);
            w.U8(d.Kind == EngineSettingKind.Number ? (byte)0 : (byte)1);
            w.U8(d.ChangesLatency ? (byte)1 : (byte)0);
            if (d.Kind == EngineSettingKind.Number)
            {
                w.F64(d.Default.Number);
                w.F64(d.Min);
                w.F64(d.Max);
                w.F64(d.Stops is { Count: > 0 } ? 0 : d.Step);
                w.Str(d.UnitKey ?? "");
                var stops = d.Stops ?? Array.Empty<double>();
                w.Count8(stops.Count, 128, "Stop");
                foreach (var s in stops) w.F64(s);
            }
            else
            {
                w.Str(d.Default.Choice ?? "");
                var options = d.Options ?? Array.Empty<EngineSettingOption>();
                if (options.Count < 1) throw new ArgumentException("A Choice needs at least one option.");
                w.Count8(options.Count, 32, "Option");
                foreach (var o in options) { w.Str(o.Name); w.Str(o.LabelKey); }
            }
        }

        /// <summary>Encode any message record as one complete wire message.</summary>
        public static byte[] Encode(object message)
        {
            var w = new Writer();
            switch (message)
            {
                case WdspHello m:
                    w.U16(m.Lowest); w.U16(m.Highest); w.Str(m.JJFlexibleVersion);
                    return w.Finish(WdspMessageType.Hello);
                case WdspHelloReject m:
                    w.U16(m.Lowest); w.U16(m.Highest); w.Str(m.Detail);
                    return w.Finish(WdspMessageType.HelloReject);
                case WdspHelloAck m:
                    w.U16(m.Version); w.Str(m.HelperVersion); w.Str(m.WdspVersion); w.Str(m.WdspCommit);
                    w.Str(m.FftwVersion); w.U16(m.MaxChannels);
                    w.Count8(m.Fingerprints.Count, 32, "Fingerprint");
                    foreach (var f in m.Fingerprints)
                    {
                        if (f.Sha256.Length != 32) throw new ArgumentException("A fingerprint is 32 bytes.");
                        w.Str(f.EngineId); w.U8(f.Slot); w.Str(f.Option); w.Bytes(f.Sha256);
                    }
                    if (m.Engines.Count < 1) throw new ArgumentException("A HelloAck offers at least one engine.");
                    w.Count8(m.Engines.Count, 16, "Engine");
                    foreach (var e in m.Engines)
                    {
                        w.Str(e.Id); w.U8(e.Directions); w.U32(e.SampleRate); w.U16(e.FrameLength);
                        w.U32(e.LatencySamples); w.U16(e.CalibrationFrames); w.U32(e.MedianNanos); w.U32(e.P99Nanos);
                        w.Count8(e.Settings.Count, 32, "Descriptor");
                        foreach (var d in e.Settings) WriteDescriptor(w, d);
                    }
                    return w.Finish(WdspMessageType.HelloAck);
                case WdspOpenChannel m:
                    w.U32(m.Channel); w.U8(m.Direction); w.Str(m.EngineId);
                    w.Count8(m.Initial.Count, 32, "Initial value");
                    foreach (var v in m.Initial) WriteNamed(w, v);
                    return w.Finish(WdspMessageType.OpenChannel);
                case WdspOpenChannelAck m:
                    w.U32(m.Channel); w.U16((ushort)m.Result); w.U32(m.LatencySamples);
                    w.Count8(m.Applied.Count, 255, "Applied value");
                    foreach (var v in m.Applied) WriteNamed(w, v);
                    w.Str(m.Detail);
                    return w.Finish(WdspMessageType.OpenChannelAck);
                case WdspCloseChannel m:
                    w.U32(m.Channel);
                    return w.Finish(WdspMessageType.CloseChannel);
                case WdspCloseChannelAck m:
                    w.U32(m.Channel); w.U16((ushort)m.Result);
                    return w.Finish(WdspMessageType.CloseChannelAck);
                case WdspFrame m:
                    w.U32(m.Channel); w.U64(m.Seq); w.U16((ushort)m.Samples.Length);
                    foreach (var s in m.Samples) w.F32(s);
                    return w.Finish(WdspMessageType.Frame);
                case WdspFrameOut m:
                    w.U32(m.Channel); w.U64(m.Seq); w.U32(m.ProcessingNanos); w.U16((ushort)m.Samples.Length);
                    foreach (var s in m.Samples) w.F32(s);
                    return w.Finish(WdspMessageType.FrameOut);
                case WdspSetSetting m:
                    w.U32(m.Channel); WriteNamed(w, m.Setting);
                    return w.Finish(WdspMessageType.SetSetting);
                case WdspSettingAck m:
                    w.U32(m.Channel); w.U16((ushort)m.Result); w.Str(m.Name); WriteValue(w, m.Value);
                    return w.Finish(WdspMessageType.SettingAck);
                case WdspHeartbeat:
                    return w.Finish(WdspMessageType.Heartbeat);
                case WdspError m:
                    w.U32(m.Channel); w.U16((ushort)m.Code); w.Str(m.Text);
                    return w.Finish(WdspMessageType.Error);
                default:
                    throw new ArgumentException("Not a Contract 2 message: " + message?.GetType().Name);
            }
        }

        /// <summary>Bytes a <see cref="EncodeFrame"/> or <see cref="EncodeFrameOut"/> message takes.</summary>
        public static int FrameSize(int samples) => 4 + 2 + 4 + 8 + 2 + 4 * samples;
        public static int FrameOutSize(int samples) => FrameSize(samples) + 4;

        /// <summary>
        /// Encode a Frame into a caller's buffer, allocating nothing. The
        /// writer thread uses this so a steady stream of frames costs no garbage.
        /// </summary>
        public static int EncodeFrame(Span<byte> dest, uint channel, ulong seq, ReadOnlySpan<float> samples)
        {
            int size = FrameSize(samples.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)(size - 4));
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4), (ushort)WdspMessageType.Frame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(6), channel);
            BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(10), seq);
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(18), (ushort)samples.Length);
            for (int i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(20 + 4 * i), samples[i]);
            return size;
        }

        /// <summary>Encode a FrameOut into a caller's buffer, allocating nothing. The stub uses it.</summary>
        public static int EncodeFrameOut(Span<byte> dest, uint channel, ulong seq, uint nanos, ReadOnlySpan<float> samples)
        {
            int size = FrameOutSize(samples.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dest, (uint)(size - 4));
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4), (ushort)WdspMessageType.FrameOut);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(6), channel);
            BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(10), seq);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(18), nanos);
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(22), (ushort)samples.Length);
            for (int i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(dest.Slice(24 + 4 * i), samples[i]);
            return size;
        }

        // ------------------------------------------------------------------
        // Decoding
        // ------------------------------------------------------------------

        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _p;
            private int _at;
            public Reader(ReadOnlySpan<byte> payload) { _p = payload; _at = 0; }
            public int Remaining => _p.Length - _at;
            private ReadOnlySpan<byte> Take(int n)
            {
                if (n < 0 || n > Remaining) throw new WdspProtocolException("A field runs past the end of the payload.");
                var s = _p.Slice(_at, n); _at += n; return s;
            }
            public byte U8() => Take(1)[0];
            public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
            public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
            public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            public byte[] Bytes(int n) => Take(n).ToArray();
            public string Str()
            {
                int n = U16();
                if (n > MaxString) throw new WdspProtocolException("A string is longer than 4,096 bytes.");
                var bytes = Take(n);
                try { return Strict.GetString(bytes); }
                catch (DecoderFallbackException) { throw new WdspProtocolException("A string is not valid UTF-8."); }
            }
            public string Name(string what)
            {
                var s = Str();
                if (!IsName(s)) throw new WdspProtocolException($"{what} '{s}' breaks the name rule.");
                return s;
            }
            public string Key(string what, bool emptyAllowed = false)
            {
                var s = Str();
                if (!(emptyAllowed && s.Length == 0) && !IsKey(s)) throw new WdspProtocolException($"{what} '{s}' breaks the key rule.");
                return s;
            }
            public string Version(string what)
            {
                var s = Str();
                if (!IsVersion(s)) throw new WdspProtocolException($"{what} '{s}' breaks the version string rule.");
                return s;
            }
            /// <summary>
            /// Before an array of <paramref name="count"/> items is made: even at
            /// the least each item can take, do they fit in what is left?
            /// </summary>
            public void Fits(int count, int leastEach, string what)
            {
                if ((long)count * leastEach > Remaining)
                    throw new WdspProtocolException($"{count} {what} cannot fit in the {Remaining} bytes left (each takes at least {leastEach}).");
            }
            public void End()
            {
                if (Remaining != 0) throw new WdspProtocolException($"The payload has {Remaining} bytes past its layout.");
            }
        }

        // The least bytes one item of each variable-count array can take, from
        // the appendix's layouts: a string is 2 bytes of count and then at
        // least 1 byte where its rule needs one (names, keys), 0 where it may be
        // empty (a fingerprint's option, a unit key).
        internal const int LeastName = 2 + 1;
        internal const int LeastNamedValue = LeastName + 1 + LeastName;              // a choice value is the shortest: kind, then a name
        internal const int LeastStop = 8;
        internal const int LeastOption = LeastName + LeastName;                      // option name, label key
        internal const int LeastDescriptor = LeastName + LeastName + 1 + 1 + LeastName + 1 + LeastOption; // a Choice with one option is the shortest
        internal const int LeastFingerprint = LeastName + 1 + 2 + 32;
        internal const int LeastEngineOffer = LeastName + 1 + 4 + 2 + 4 + 2 + 4 + 4 + 1;

        private static WdspValue ReadValue(ref Reader r, bool noneAllowed)
        {
            byte kind = r.U8();
            switch (kind)
            {
                case WdspValue.NumberKind: return new(kind, r.F64(), null);
                case WdspValue.ChoiceKind: return new(kind, 0, r.Name("A choice"));
                case WdspValue.NoneKind:
                    if (!noneAllowed) throw new WdspProtocolException("A value of kind none where none is not allowed.");
                    return WdspValue.None;
                default: throw new WdspProtocolException("Value kind " + kind + " is out of range.");
            }
        }

        private static WdspNamedValue ReadNamed(ref Reader r) => new(r.Name("A setting name"), ReadValue(ref r, false));

        private static EngineSettingDescriptor ReadDescriptor(ref Reader r)
        {
            string name = r.Name("A setting name");
            string label = r.Key("A label key");
            byte kind = r.U8();
            byte flags = r.U8();
            bool changesLatency = (flags & 1) != 0; // every other bit is ignored in version 1
            if (kind == 0)
            {
                double def = r.F64(), min = r.F64(), max = r.F64(), step = r.F64();
                string unit = r.Key("A unit key", emptyAllowed: true);
                int stopCount = r.U8();
                if (stopCount > 128) throw new WdspProtocolException("A stop count above 128.");
                r.Fits(stopCount, LeastStop, "stops");
                double[]? stops = stopCount == 0 ? null : new double[stopCount];
                for (int i = 0; i < stopCount; i++) stops![i] = r.F64();
                return new EngineSettingDescriptor(name, label, EngineSettingKind.Number, new EngineSettingValue(def),
                    min, max, step, unit.Length == 0 ? null : unit, stops, null, changesLatency);
            }
            if (kind == 1)
            {
                string def = r.Name("A default option");
                int count = r.U8();
                if (count < 1 || count > 32) throw new WdspProtocolException("An option count outside 1 to 32.");
                r.Fits(count, LeastOption, "options");
                var options = new EngineSettingOption[count];
                for (int i = 0; i < count; i++) options[i] = new EngineSettingOption(r.Name("An option name"), r.Key("An option label key"));
                return new EngineSettingDescriptor(name, label, EngineSettingKind.Choice, new EngineSettingValue(0, def),
                    Options: options, ChangesLatency: changesLatency);
            }
            throw new WdspProtocolException("Descriptor kind " + kind + " is out of range.");
        }

        /// <summary>
        /// Decode one whole message's payload. Throws <see cref="WdspProtocolException"/>
        /// for every protocol error: a wrong-length payload, a count or string
        /// past the end, invalid UTF-8, a broken string rule, a kind out of
        /// range, or a known type from the wrong sender.
        /// </summary>
        public static object Decode(WdspMessageType type, ReadOnlySpan<byte> payload, WdspRole receiver)
        {
            if (type == WdspMessageType.Invalid) throw new WdspProtocolException("Type 0 is never valid.", WdspResult.UnexpectedMessage);
            var sender = receiver == WdspRole.JJFlexible ? WdspRole.Helper : WdspRole.JJFlexible;
            if (!SentBy(type, sender))
                throw new WdspProtocolException($"{type} may not come from this end.", WdspResult.UnexpectedMessage);
            var r = new Reader(payload);
            switch (type)
            {
                case WdspMessageType.Hello:
                {
                    var m = new WdspHello(r.U16(), r.U16(), r.Version("The JJ Flexible version"));
                    if (m.Highest < m.Lowest) throw new WdspProtocolException("Hello's highest version is below its lowest.");
                    return m; // trailing bytes are ignored: a later version may extend Hello
                }
                case WdspMessageType.HelloReject:
                {
                    ushort lo = r.U16(), hi = r.U16();
                    return new WdspHelloReject(lo, hi, r.Str()); // trailing bytes ignored, as for Hello
                }
                case WdspMessageType.HelloAck:
                {
                    ushort version = r.U16();
                    if (version != ProtocolVersion)
                        throw new WdspProtocolException($"HelloAck chose version {version}, which this end does not speak.");
                    string helper = r.Version("The helper version"), wdsp = r.Version("The WDSP version");
                    string commit = r.Version("The WDSP commit"), fftw = r.Version("The FFTW version");
                    ushort maxChannels = r.U16();
                    int fpCount = r.U8();
                    if (fpCount > 32) throw new WdspProtocolException("More than 32 model fingerprints.");
                    r.Fits(fpCount, LeastFingerprint, "fingerprints");
                    var fps = new WdspFingerprint[fpCount];
                    for (int i = 0; i < fpCount; i++)
                    {
                        string engine = r.Name("A fingerprint's engine id");
                        byte slot = r.U8();
                        string option = r.Str();
                        if (option.Length != 0 && !IsName(option)) throw new WdspProtocolException("A fingerprint's option breaks the name rule.");
                        fps[i] = new WdspFingerprint(engine, slot, option, r.Bytes(32));
                    }
                    int engineCount = r.U8();
                    if (engineCount < 1 || engineCount > 16) throw new WdspProtocolException("An engine count outside 1 to 16.");
                    r.Fits(engineCount, LeastEngineOffer, "engine offers");
                    var engines = new WdspEngineOffer[engineCount];
                    for (int i = 0; i < engineCount; i++)
                    {
                        string id = r.Name("An engine id");
                        byte dirs = r.U8();
                        uint rate = r.U32();
                        ushort frame = r.U16();
                        uint latency = r.U32();
                        ushort calibration = r.U16();
                        uint median = r.U32(), p99 = r.U32();
                        int descriptors = r.U8();
                        if (descriptors > 32) throw new WdspProtocolException("More than 32 descriptors.");
                        r.Fits(descriptors, LeastDescriptor, "descriptors");
                        var list = new EngineSettingDescriptor[descriptors];
                        for (int d = 0; d < descriptors; d++) list[d] = ReadDescriptor(ref r);
                        engines[i] = new WdspEngineOffer(id, dirs, rate, frame, latency, calibration, median, p99, list);
                    }
                    r.End();
                    return new WdspHelloAck(version, helper, wdsp, commit, fftw, maxChannels, fps, engines);
                }
                case WdspMessageType.OpenChannel:
                {
                    uint ch = r.U32(); byte dir = r.U8(); string engine = r.Name("An engine id");
                    int count = r.U8();
                    if (count > 32) throw new WdspProtocolException("More than 32 initial values.");
                    r.Fits(count, LeastNamedValue, "initial values");
                    var values = new WdspNamedValue[count];
                    for (int i = 0; i < count; i++) values[i] = ReadNamed(ref r);
                    r.End();
                    return new WdspOpenChannel(ch, dir, engine, values);
                }
                case WdspMessageType.OpenChannelAck:
                {
                    uint ch = r.U32(); var result = (WdspResult)r.U16(); uint latency = r.U32();
                    int count = r.U8();
                    r.Fits(count, LeastNamedValue, "applied values");
                    var values = new WdspNamedValue[count];
                    for (int i = 0; i < count; i++) values[i] = ReadNamed(ref r);
                    string detail = r.Str();
                    r.End();
                    return new WdspOpenChannelAck(ch, result, latency, values, detail);
                }
                case WdspMessageType.CloseChannel:
                {
                    var m = new WdspCloseChannel(r.U32()); r.End(); return m;
                }
                case WdspMessageType.CloseChannelAck:
                {
                    var m = new WdspCloseChannelAck(r.U32(), (WdspResult)r.U16()); r.End(); return m;
                }
                case WdspMessageType.Frame:
                {
                    uint ch = r.U32(); ulong seq = r.U64(); int n = r.U16();
                    if (n * 4 > r.Remaining) throw new WdspProtocolException("The sample count runs past the payload.");
                    var samples = new float[n];
                    for (int i = 0; i < n; i++) samples[i] = r.F32();
                    r.End();
                    return new WdspFrame(ch, seq, samples);
                }
                case WdspMessageType.FrameOut:
                {
                    uint ch = r.U32(); ulong seq = r.U64(); uint nanos = r.U32(); int n = r.U16();
                    if (n * 4 > r.Remaining) throw new WdspProtocolException("The sample count runs past the payload.");
                    var samples = new float[n];
                    for (int i = 0; i < n; i++) samples[i] = r.F32();
                    r.End();
                    return new WdspFrameOut(ch, seq, nanos, samples);
                }
                case WdspMessageType.SetSetting:
                {
                    uint ch = r.U32(); var v = ReadNamed(ref r); r.End();
                    return new WdspSetSetting(ch, v);
                }
                case WdspMessageType.SettingAck:
                {
                    uint ch = r.U32(); var result = (WdspResult)r.U16(); string name = r.Name("A setting name");
                    bool noneAllowed = result is WdspResult.UnknownChannel or WdspResult.UnknownSetting;
                    var value = ReadValue(ref r, noneAllowed);
                    r.End();
                    return new WdspSettingAck(ch, result, name, value);
                }
                case WdspMessageType.Heartbeat:
                    r.End();
                    return WdspHeartbeat.Instance;
                case WdspMessageType.Error:
                {
                    uint ch = r.U32(); var code = (WdspResult)r.U16(); string text = r.Str(); r.End();
                    return new WdspError(ch, code, text);
                }
                default:
                    throw new WdspProtocolException("Type " + (ushort)type + " has no decoder.", WdspResult.UnexpectedMessage);
            }
        }

        /// <summary>
        /// The FrameOut fast path for JJ Flexible's reader thread: reads the
        /// header and checks the length, leaving the samples in place so the
        /// caller can validate and copy them with no allocation. Same rules as
        /// <see cref="Decode"/>.
        /// </summary>
        public static void ReadFrameOutHeader(ReadOnlySpan<byte> payload, out uint channel, out ulong seq,
            out uint nanos, out int count)
        {
            if (payload.Length < 18) throw new WdspProtocolException("A FrameOut shorter than its header.");
            channel = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            seq = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(4));
            nanos = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12));
            count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(16));
            if (18 + 4 * count != payload.Length)
                throw new WdspProtocolException("A FrameOut whose sample count does not match its length.");
        }

        /// <summary>Sample <paramref name="i"/> of a FrameOut payload whose header <see cref="ReadFrameOutHeader"/> accepted.</summary>
        public static float FrameOutSample(ReadOnlySpan<byte> payload, int i) =>
            BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(18 + 4 * i));

        // ------------------------------------------------------------------
        // Validation of an offer: "invalid as a whole"
        // ------------------------------------------------------------------

        /// <summary>
        /// Null when the HelloAck keeps every rule; otherwise the first rule it
        /// breaks, in words for the trace. An invalid HelloAck makes JJ Flexible
        /// treat the helper as a version mismatch, never retried.
        /// </summary>
        public static string? ValidateHelloAck(WdspHelloAck ack)
        {
            if (ack.Version != ProtocolVersion) return $"version {ack.Version} is not spoken";
            if (ack.WdspCommit.Length != 40 || !IsLowerHex(ack.WdspCommit)) return "the WDSP commit is not 40 lower-case hex characters";
            if (ack.MaxChannels < 16) return $"at most {ack.MaxChannels} channels, below the floor of 16";
            foreach (var f in ack.Fingerprints)
                if (f.Sha256.Length != 32) return "a fingerprint is not 32 bytes";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ack.Engines)
            {
                if (!ids.Add(e.Id)) return $"engine {e.Id} is offered twice";
                if ((e.Directions & ~3) != 0) return $"engine {e.Id} sets direction bits other than 0 and 1";
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in e.Settings)
                {
                    if (!names.Add(d.Name)) return $"engine {e.Id} lists setting {d.Name} twice";
                    string? bad = ValidateDescriptor(d);
                    if (bad != null) return $"engine {e.Id} setting {d.Name}: {bad}";
                }
            }
            return null;
        }

        private static bool IsLowerHex(string s)
        {
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>
        /// Null when an Ok OpenChannelAck's applied values are what the appendix
        /// says they must be: every descriptor of the engine, in the offer's
        /// order, each once, each with a value its own descriptor accepts.
        /// Otherwise the first rule broken, for the trace.
        /// </summary>
        public static string? CheckApplied(IReadOnlyList<EngineSettingDescriptor> offer, IReadOnlyList<WdspNamedValue> applied)
        {
            if (applied.Count != offer.Count)
                return $"applied {applied.Count} value{(applied.Count == 1 ? "" : "s")}; the offer has {offer.Count} setting{(offer.Count == 1 ? "" : "s")}";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < applied.Count; i++)
            {
                string name = applied[i].Name;
                if (!seen.Add(name)) return $"setting {name} is applied twice";
                int at = -1;
                for (int d = 0; d < offer.Count; d++) if (string.Equals(offer[d].Name, name, StringComparison.Ordinal)) { at = d; break; }
                if (at < 0) return $"the offer has no setting {name}";
                if (at != i) return $"setting {name} is out of the offer's order";
                if (!offer[at].Accepts(applied[i].Value.ToSetting())) return $"setting {name}'s own descriptor refuses the value applied";
            }
            return null;
        }

        public static string? ValidateDescriptor(EngineSettingDescriptor d)
        {
            if (d.Kind == EngineSettingKind.Choice)
            {
                if (d.Options == null || d.Options.Count == 0) return "a Choice with no options";
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in d.Options) if (!names.Add(o.Name)) return $"option {o.Name} appears twice";
                if (d.Default.Choice == null || !names.Contains(d.Default.Choice)) return "the default is not one of the options";
                return null;
            }
            if (!double.IsFinite(d.Default.Number) || !double.IsFinite(d.Min) || !double.IsFinite(d.Max) || !double.IsFinite(d.Step))
                return "a number that is not finite";
            if (d.Min > d.Max) return "the minimum is above the maximum";
            if (d.Stops is { Count: > 0 } stops)
            {
                for (int i = 0; i < stops.Count; i++)
                {
                    if (!double.IsFinite(stops[i])) return "a stop that is not finite";
                    if (i > 0 && !(stops[i] > stops[i - 1])) return "the stops are not strictly ascending";
                }
                if (stops[0] != d.Min || stops[^1] != d.Max) return "the stops do not run from the minimum to the maximum";
                if (BitConverter.DoubleToInt64Bits(d.Step) != 0) return "stops and a step that is not sent as 0";
            }
            else if (!(d.Step > 0)) return "no stops and a step that is not above zero";
            if (!d.Accepts(d.Default)) return "the default fails the acceptance rule";
            return null;
        }

        // ------------------------------------------------------------------
        // The mapping onto Unavailability (appendix: "How the codes and
        // conditions map"). Null means "no change to availability".
        // ------------------------------------------------------------------

        /// <summary>
        /// What an <c>OpenChannelAck</c> result does to an engine. Ok is None;
        /// null means the result changes nothing (traced as JJ Flexible's own
        /// bug). A code this end does not know is treated as Internal.
        /// </summary>
        public static Unavailability? MapOpenResult(WdspResult result) => result switch
        {
            WdspResult.Ok => Unavailability.None,
            WdspResult.EngineNotOffered or WdspResult.DirectionNotServed => Unavailability.HelperVersionMismatch,
            WdspResult.ChannelBuildFailed or WdspResult.TooManyChannels or WdspResult.Internal => Unavailability.InitFailed,
            WdspResult.UnknownSetting or WdspResult.InvalidValue or WdspResult.ChannelIdInUse or WdspResult.UnknownChannel => null,
            _ => Unavailability.InitFailed, // unknown or inapplicable: Internal
        };

        /// <summary>True when a code is one this end knows.</summary>
        public static bool IsKnownResult(WdspResult result) => Enum.IsDefined(result);

        /// <summary>What a condition does to every engine it touches; null means no change.</summary>
        public static Unavailability? MapCondition(WdspCondition condition) => condition switch
        {
            WdspCondition.HelloReject or WdspCondition.InvalidHelloAck or WdspCondition.VersionNotSpoken or
            WdspCondition.EngineNotOffered or WdspCondition.DirectionNotServed or WdspCondition.WrongSampleRate or
            WdspCondition.WrongFrameLength or WdspCondition.ReopenLatencyDiffers => Unavailability.HelperVersionMismatch,
            WdspCondition.Stall or WdspCondition.BrokenPipe or WdspCondition.ProcessExited or
            WdspCondition.FatalProtocolError or WdspCondition.HandshakeDeadline or
            WdspCondition.StartFailed => Unavailability.HelperNotRunning,
            WdspCondition.FileMissing or WdspCondition.StartBlocked => Unavailability.HelperMissing,
            WdspCondition.SettingRefused or WdspCondition.NonFatalError => null,
            _ => null,
        };

        /// <summary>True when a condition follows the restart policy and counts toward its cap.</summary>
        public static bool CountsTowardCap(WdspCondition condition) =>
            MapCondition(condition) == Unavailability.HelperNotRunning;
    }
}
