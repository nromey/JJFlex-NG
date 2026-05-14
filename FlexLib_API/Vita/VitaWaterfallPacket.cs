// ****************************************************************************
///*!	\file VitaWaterfallPacket.cs
// *	\brief Defines a Vita Extended Data Packet for waterfall data
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2014-03-11
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Buffers.Binary;
using System.Diagnostics;

using Flex.Util;

namespace Flex.Smoothlake.Vita
{
    public class VitaWaterfallPacket : VitaPacketBase
    {
        public WaterfallTile tile; // will there be more than 1 tile per packet??

        public VitaWaterfallPacket()
        {
            Header.pkt_type = VitaPacketType.ExtDataWithStream;
            Header.c = true;
            Header.t = true;
            Header.tsi = VitaTimeStampIntegerType.Other;
            Header.tsf = VitaTimeStampFractionalType.RealTime;
        }

        public VitaWaterfallPacket(byte[] data)
        {
            int index = ParsePreamble(data);

            // allocate the waterfall tile
            tile = new WaterfallTile();
            tile.DateTime = DateTime.UtcNow;

            // populate the fields of the tile — read big-endian without mutating the input buffer
            tile.FrameLowFreq = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(index));
            index += 8;

            tile.BinBandwidth = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(index));
            index += 8;

            tile.LineDurationMS = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;

            tile.Width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            tile.Height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            tile.Timecode = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;

            tile.AutoBlackLevel = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;

            tile.TotalBinsInFrame = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            tile.FirstBinIndex = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
            index += 2;

            int payload_bytes = CalculatePayloadBytes();

            payload_bytes -= 32; // payload header

            // Data.Length is the number of elements in the ushort Data array
            // and can be sent between multiple packets
            // ushort is 2 bytes

            // Debug.Assert(payload_bytes == tile.Data.Length * sizeof(ushort)); // Fails on resize ?
            tile.Data = new ushort[tile.Width * tile.Height];
            
            if (tile.Data.Length * 2 < payload_bytes)
                payload_bytes = tile.Data.Length * 2;

            // Copy payload and swap endianness in one pass — avoids per-element Array.Reverse
            int elemCount = payload_bytes / 2;
            for (int i = 0; i < elemCount; i++)
            {
                int off = index + i * 2;
                tile.Data[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off));
            }

            index += payload_bytes;

            ParseTrailer(data, index);
        }

        public byte[] ToBytes()
        {
            int num_bytes = 4 + 4 + 28 + tile.Data.Length / 2; // header + stream_id + payload header
            if (num_bytes % 4 == 2) num_bytes += 2; // handle word boundary

            if (Header.c) num_bytes += 8;
            if (Header.t) num_bytes += 4;
            if (Header.tsi != VitaTimeStampIntegerType.None) num_bytes += 4;
            if (Header.tsf != VitaTimeStampFractionalType.None) num_bytes += 8;

            byte[] temp = new byte[num_bytes];
            int index = WriteHeaderBytes(temp);

            BinaryPrimitives.WriteInt64BigEndian(temp.AsSpan(index), tile.FrameLowFreq);
            index += 8;

            BinaryPrimitives.WriteInt64BigEndian(temp.AsSpan(index), tile.BinBandwidth);
            index += 8;

            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), tile.LineDurationMS);
            index += 4;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), tile.Width);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), tile.Height);
            index += 2;

            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), tile.Timecode);
            index += 4;

            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), tile.AutoBlackLevel);
            index += 4;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)tile.TotalBinsInFrame);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), (ushort)tile.FirstBinIndex);
            index += 2;

            Array.Copy(tile.Data, 0, temp, index, tile.Data.Length);
            index += tile.Data.Length;

            WriteTrailerBytes(temp, index);

            return temp;
        }
    }
}
