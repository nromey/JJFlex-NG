// ****************************************************************************
///*!	\file VitaPacketPreamble.cs
// *	\brief Defines the typical Vita Header (Preamble)
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
using System.Net;

namespace Flex.Smoothlake.Vita
{
    /// <summary>
    /// Represents a single Vita IF Data Packet as defined in the Vita 49 Standard Section 6.1.
    /// Can also represent an Extended Data Packet as seen in Section 6.2.
    /// </summary>
    public struct VitaPacketPreamble
    {
        public Header header;
        public uint stream_id;
        public VitaClassID class_id;
        public uint timestamp_int;
        public ulong timestamp_frac;

        // public VitaPacketPreamble()
        // {
        //     header = new Header();
        //     header.pkt_type = VitaPacketType.IFDataWithStream;
        //     header.c = true;
        //     header.t = true;
        //     header.tsi = VitaTimeStampIntegerType.Other;
        //     header.tsf = VitaTimeStampFractionalType.RealTime;
        // }

        public VitaPacketPreamble(byte[] data)
        {
            int index = 0;
            uint temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;

            header = new Header
            {
                pkt_type = (VitaPacketType)(temp >> 28),
                c = ((temp & 0x08000000) != 0),
                t = ((temp & 0x04000000) != 0),
                tsi = (VitaTimeStampIntegerType)((temp >> 22) & 0x03),
                tsf = (VitaTimeStampFractionalType)((temp >> 20) & 0x03),
                packet_count = (byte)((temp >> 16) & 0x0F),
                packet_size = (ushort)(temp & 0xFFFF)
            };

            // if packet is a type with a stream id, read/save it
            if (header.pkt_type == VitaPacketType.IFDataWithStream ||
                header.pkt_type == VitaPacketType.ExtDataWithStream)
            {
                stream_id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
                index += 4;
            }
            else
            {
                stream_id = 0;
            }

            if (header.c)
            {
                temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
                index += 4;
                class_id.OUI = temp & 0x00FFFFFF;

                temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
                index += 4;
                class_id.InformationClassCode = (ushort)(temp >> 16);
                class_id.PacketClassCode = (ushort)temp;
            }
            else
            {
                class_id = new VitaClassID();
            }

            if (header.tsi != VitaTimeStampIntegerType.None)
            {
                timestamp_int = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
                index += 4;
            }
            else
            {
                timestamp_int = 0;
            }

            if (header.tsf != VitaTimeStampFractionalType.None)
            {
                timestamp_frac = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(index));
                index += 8;
            }
            else
            {
                timestamp_frac = 0;
            }
        }

        public byte[] ToBytes()
        {
            int index = 0;

            int num_bytes = 4 + 4; // header + stream_id + payload
            if (header.c) num_bytes += 8;
            if (header.tsi != VitaTimeStampIntegerType.None) num_bytes += 4;
            if (header.tsf != VitaTimeStampFractionalType.None) num_bytes += 8;

            byte[] temp = new byte[num_bytes];
            byte b = (byte)((byte)header.pkt_type << 4 |
                Convert.ToByte(header.c) << 3 |
                Convert.ToByte(header.t) << 2);
            temp[0] = b;

            b = (byte)((byte)header.tsi << 6 |
                (byte)header.tsf << 4 |
                (byte)header.packet_count);
            temp[1] = b;

            temp[2] = (byte)(header.packet_size >> 8);
            temp[3] = (byte)header.packet_size;

            index += 4;

            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), stream_id);
            index += 4;

            if (header.c)
            {
                BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), class_id.OUI);
                index += 4;

                BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), class_id.InformationClassCode);
                index += 2;

                BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), class_id.PacketClassCode);
                index += 2;
            }

            if (header.tsi != VitaTimeStampIntegerType.None)
            {
                BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), timestamp_int);
                index += 4;
            }

            if (header.tsf != VitaTimeStampFractionalType.None)
            {
                BinaryPrimitives.WriteUInt64BigEndian(temp.AsSpan(index), timestamp_frac);
                index += 8;
            }

            return temp;
        }
    }
}
