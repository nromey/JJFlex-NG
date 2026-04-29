// ****************************************************************************
///*!	\file VitaFFTPacket.cs
// *	\brief Defines a Vita Extended Data Packet for FFTs
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

namespace Flex.Smoothlake.Vita
{
    public class VitaFFTPacket : VitaPacketBase
    {
        public uint start_bin_index;
        public uint num_bins;
        public uint bin_size; // in bytes
        public uint frame_index;
        public uint total_bins_in_frame;
        public ushort[] payload;

        private int words_in_packet_header = 10;

        public VitaFFTPacket()
        {
            Header.pkt_type = VitaPacketType.ExtDataWithStream;
            Header.c = true;
            Header.t = true;
            Header.tsi = VitaTimeStampIntegerType.Other;
            Header.tsf = VitaTimeStampFractionalType.RealTime;
        }

        public VitaFFTPacket(byte[] data)
        {
            int index = ParsePreamble(data);

            start_bin_index = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            num_bins = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            bin_size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            total_bins_in_frame = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            frame_index = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;

            int payload_bytes = (int)(num_bins * bin_size);
            payload = new ushort[num_bins];
            //Array.Copy(data, index, payload, 0, payload_bytes);
            Buffer.BlockCopy(data, index, payload, 0, payload_bytes);

            // swap endianness on the bins
            for (int i = 0; i < num_bins; i++)
                payload[i] = BinaryPrimitives.ReverseEndianness(payload[i]);

            index += payload_bytes;

            ParseTrailer(data, index);
        }

        public byte[] ToBytes()
        {
            int num_bytes = (words_in_packet_header * 4) + payload.Length; // header + stream_id + start bin/num payload
            if (Header.c) num_bytes += 8;
            if (Header.t) num_bytes += 4;
            if (Header.tsi != VitaTimeStampIntegerType.None) num_bytes += 4;
            if (Header.tsf != VitaTimeStampFractionalType.None) num_bytes += 8;

            byte[] temp = new byte[num_bytes];
            int index = WriteHeaderBytes(temp);

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)start_bin_index);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)num_bins);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)bin_size);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)total_bins_in_frame);
            index += 2;

            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), frame_index);
            index += 4;

            Array.Copy(payload, 0, temp, index, payload.Length);
            index += payload.Length;

            WriteTrailerBytes(temp, index);

            return temp;
        }
    }
}
