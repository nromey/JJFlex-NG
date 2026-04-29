// ****************************************************************************
///*!	\file VitaDataPacket.cs
// *	\brief Defines a Vita IF Data Packet
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2012-03-05
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Flex.Smoothlake.Vita
{
    /// <summary>
    /// Represents a single Vita IF Data Packet as defined in the Vita 49 Standard Section 6.1.
    /// Can also represent an Extended Data Packet as seen in Section 6.2.
    /// </summary>
    public class VitaIFDataPacket : VitaPacketBase
    {
        public float[] payload;
        public Int16[] payload_int16;

        public int Length;

        public VitaIFDataPacket()
        {
            Header.pkt_type = VitaPacketType.IFDataWithStream;
            Header.c = true;
            Header.t = true;
            Header.tsi = VitaTimeStampIntegerType.Other;
            Header.tsf = VitaTimeStampFractionalType.RealTime;
        }

        private static readonly float ONE_OVER_ZERO_DBFS = 1.0f / (1 << 15);
        private static readonly float ONE_OVER_INT16_MAX = 1.0f / (float)Int16.MaxValue;

        public VitaIFDataPacket(byte[] data, int bytes)
        {
            Length = bytes;
            ParseInto(data, bytes, null);
        }

        /// <summary>
        /// Parses a Vita IF Data Packet from raw bytes into this instance, reusing the provided
        /// payload buffer when possible to avoid per-packet allocations.
        /// </summary>
        /// <param name="data">Raw packet bytes</param>
        /// <param name="bytes">Number of valid bytes in data</param>
        /// <param name="reusablePayload">Pre-allocated float array to reuse for payload, or null to allocate new</param>
        public void ParseInto(byte[] data, int bytes, float[] reusablePayload)
        {
            Length = bytes;

            int index = ParsePreamble(data);
            int payloadBytes = CalculatePayloadBytes();

            switch (ClassId.PacketClassCode)
            {
                // swap endianess on the bytes
                // for each sample if we are dealing with DAX audio.
                case VitaFlex.SL_VITA_IF_NARROW_CLASS:
                {
                    var floatCount = payloadBytes / sizeof(float);
                    payload = EnsurePayloadSize(reusablePayload, floatCount);

                    Debug.Assert(payloadBytes % 4 == 0);

                    // Swap endianness during copy — does not mutate the input data array
                    ref byte dataRef = ref data[index];
                    for (var i = 0; i < floatCount; i++)
                    {
                        var intVal = BinaryPrimitives.ReverseEndianness(
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, i * 4)));
                        payload[i] = Unsafe.As<uint, float>(ref intVal);
                    }

                    break;
                }
                case VitaFlex.SL_VITA_IF_NARROW_REDUCED_BW_CLASS:
                {
                    // Int16 Mono Samples — merge network-to-host, int16-to-float, and mono-to-stereo in one pass
                    var sampleCount = payloadBytes / sizeof(short);
                    var stereoCount = sampleCount * 2;
                    payload = EnsurePayloadSize(reusablePayload, stereoCount);

                    ref byte dataRef = ref data[index];
                    for (var i = 0; i < sampleCount; i++)
                    {
                        var val = BinaryPrimitives.ReverseEndianness(
                            Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref dataRef, i * 2)));
                        var fval = val * ONE_OVER_INT16_MAX;
                        payload[i * 2] = fval;
                        payload[i * 2 + 1] = fval;
                    }

                    break;
                }
                case VitaFlex.SL_VITA_IF_WIDE_CLASS_192kHz:
                case VitaFlex.SL_VITA_IF_WIDE_CLASS_96kHz:
                case VitaFlex.SL_VITA_IF_WIDE_CLASS_48kHz:
                case VitaFlex.SL_VITA_IF_WIDE_CLASS_24kHz:
                {
                    var floatCount = payloadBytes / sizeof(float);
                    payload = EnsurePayloadSize(reusablePayload, floatCount);

                    // Copy the data as is — it is already floating point — then scale
                    Buffer.BlockCopy(data, index, payload, 0, payloadBytes);

                    var scaleVec = new Vector<float>(ONE_OVER_ZERO_DBFS);
                    int vecSize = Vector<float>.Count;
                    var i = 0;
                    for (; i <= floatCount - vecSize; i += vecSize)
                    {
                        var v = new Vector<float>(payload, i);
                        (v * scaleVec).CopyTo(payload, i);
                    }
                    for (; i < floatCount; i++)
                        payload[i] *= ONE_OVER_ZERO_DBFS;

                    break;
                }
            }

            index += payloadBytes;

            ParseTrailer(data, index);
        }

        /// <summary>
        /// Returns the reusable buffer if it is exactly the right size, otherwise allocates a new one.
        /// An exact match is required because consumers use payload.Length to determine sample count.
        /// </summary>
        private static float[] EnsurePayloadSize(float[] reusable, int requiredLength)
        {
            if (reusable != null && reusable.Length == requiredLength)
                return reusable;
            return new float[requiredLength];
        }

        public byte[] ToBytes(bool use_int16_payload = false)
        {
            int num_bytes = CalculatePacketSize(use_int16_payload);
            byte[] temp = new byte[num_bytes];
            WriteBytes(temp, use_int16_payload);
            return temp;
        }

        public int CalculatePacketSize(bool use_int16_payload = false)
        {
            int num_bytes = CalculatePreambleSize();
            if (use_int16_payload)
            {
                num_bytes += payload_int16.Length * sizeof(Int16);
            }
            else
            {
                num_bytes += payload.Length * sizeof(float);
            }
            if (Header.t) num_bytes += 4;
            return num_bytes;
        }

        public int WriteBytes(byte[] temp, bool use_int16_payload = false)
        {
            int index = WriteHeaderBytes(temp);

            if (use_int16_payload)
            {
                for (int i = 0; i < payload_int16.Length; i++)
                {
                    BinaryPrimitives.WriteInt16BigEndian(temp.AsSpan(index + i * 2), payload_int16[i]);
                }

                index += payload_int16.Length * sizeof(Int16);
            }
            else
            {
                for (var i = 0; i < payload.Length; i++)
                {
                    uint val;
                    unsafe
                    {
                        var f = payload[i]; 
                        val = *(uint*)&f;
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index + i * 4), val);
                }

                index += payload.Length * sizeof(float);
            }

            index = WriteTrailerBytes(temp, index);

            return index;
        }

    }
}
