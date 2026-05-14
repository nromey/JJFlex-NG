// ****************************************************************************
///*!	\file VitaPacketBase.cs
// *	\brief Base class for Vita Packets with shared header/trailer parsing
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

namespace Flex.Smoothlake.Vita;

public abstract class VitaPacketBase
{
    public Header Header;
    public uint StreamId;
    public VitaClassID ClassId;
    public uint TimestampInt;
    public ulong TimestampFrac;
    public Trailer Trailer;

    /// <summary>
    /// Parses the common preamble fields (header, stream_id, class_id, timestamps) from raw bytes.
    /// Returns the byte index immediately after the preamble.
    /// </summary>
    protected int ParsePreamble(byte[] data)
    {
        var index = 0;
        var temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
        index += 4;

        Header = new Header
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
        if (Header.pkt_type is VitaPacketType.IFDataWithStream or VitaPacketType.ExtDataWithStream)
        {
            StreamId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;
        }

        if (Header.c)
        {
            temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;
            ClassId.OUI = temp & 0x00FFFFFF;

            temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;
            ClassId.InformationClassCode = (ushort)(temp >> 16);
            ClassId.PacketClassCode = (ushort)temp;
        }

        if (Header.tsi != VitaTimeStampIntegerType.None)
        {
            TimestampInt = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
            index += 4;
        }

        if (Header.tsf != VitaTimeStampFractionalType.None)
        {
            TimestampFrac = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(index));
            index += 8;
        }

        return index;
    }

    /// <summary>
    /// Calculates the number of payload bytes based on the header fields.
    /// </summary>
    protected int CalculatePayloadBytes()
    {
        var payloadBytes = (Header.packet_size - 1) * 4; // -1 for header
        switch (Header.pkt_type)
        {
            case VitaPacketType.IFDataWithStream:
            case VitaPacketType.ExtDataWithStream:
                payloadBytes -= 4;
                break;
            case VitaPacketType.IFData:
            case VitaPacketType.ExtData:
            case VitaPacketType.IFContext:
            case VitaPacketType.ExtContext:
            default:
                break;
        }

        if (Header.c) payloadBytes -= 8;
        if (Header.tsi != VitaTimeStampIntegerType.None) payloadBytes -= 4;
        if (Header.tsf != VitaTimeStampFractionalType.None) payloadBytes -= 8;
        if (Header.t) payloadBytes -= 4;

        return payloadBytes;
    }

    /// <summary>
    /// Parses the trailer from raw bytes at the given index.
    /// </summary>
    protected void ParseTrailer(byte[] data, int index)
    {
        if (!Header.t)
            return;

        var temp = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index));
        Trailer.CalibratedTimeEnable = (temp & 0x80000000) != 0;
        Trailer.ValidDataEnable = (temp & 0x40000000) != 0;
        Trailer.ReferenceLockEnable = (temp & 0x20000000) != 0;
        Trailer.AGCMGCEnable = (temp & 0x10000000) != 0;
        Trailer.DetectedSignalEnable = (temp & 0x08000000) != 0;
        Trailer.SpectralInversionEnable = (temp & 0x04000000) != 0;
        Trailer.OverrangeEnable = (temp & 0x02000000) != 0;
        Trailer.SampleLossEnable = (temp & 0x01000000) != 0;

        Trailer.CalibratedTimeIndicator = (temp & 0x00080000) != 0;
        Trailer.ValidDataIndicator = (temp & 0x00040000) != 0;
        Trailer.ReferenceLockIndicator = (temp & 0x00020000) != 0;
        Trailer.AGCMGCIndicator = (temp & 0x00010000) != 0;
        Trailer.DetectedSignalIndicator = (temp & 0x00008000) != 0;
        Trailer.SpectralInversionIndicator = (temp & 0x00004000) != 0;
        Trailer.OverrangeIndicator = (temp & 0x00002000) != 0;
        Trailer.SampleLossIndicator = (temp & 0x00001000) != 0;

        Trailer.e = (temp & 0x80) != 0;
        Trailer.AssociatedContextPacketCount = (byte)(temp & 0xEF);
    }

    /// <summary>
    /// Writes the common header fields (header word, stream_id, class_id, timestamps) into the buffer.
    /// Returns the byte index immediately after the written preamble.
    /// </summary>
    protected int WriteHeaderBytes(byte[] temp)
    {
        var index = 0;

        var b = (byte)((byte)Header.pkt_type << 4 |
                       Convert.ToByte(Header.c) << 3 |
                       Convert.ToByte(Header.t) << 2);
        temp[0] = b;

        b = (byte)((byte)Header.tsi << 6 |
                   (byte)Header.tsf << 4 |
                   (byte)Header.packet_count);
        temp[1] = b;

        temp[2] = (byte)(Header.packet_size >> 8);
        temp[3] = (byte)Header.packet_size;

        index += 4;

        BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), StreamId);
        index += 4;

        if (Header.c)
        {
            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), ClassId.OUI);
            index += 4;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), ClassId.InformationClassCode);
            index += 2;

            BinaryPrimitives.WriteUInt16BigEndian(temp.AsSpan(index), ClassId.PacketClassCode);
            index += 2;
        }

        if (Header.tsi != VitaTimeStampIntegerType.None)
        {
            BinaryPrimitives.WriteUInt32BigEndian(temp.AsSpan(index), TimestampInt);
            index += 4;
        }

        if (Header.tsf != VitaTimeStampFractionalType.None)
        {
            BinaryPrimitives.WriteUInt64BigEndian(temp.AsSpan(index), TimestampFrac);
            index += 8;
        }

        return index;
    }

    /// <summary>
    /// Writes the trailer into the buffer at the given index.
    /// Returns the byte index immediately after the written trailer.
    /// </summary>
    protected int WriteTrailerBytes(byte[] temp, int index)
    {
        if (!Header.t)
            return index;

        var b = (byte)(Convert.ToByte(Trailer.CalibratedTimeEnable) << 7 |
                       Convert.ToByte(Trailer.ValidDataEnable) << 6 |
                       Convert.ToByte(Trailer.ReferenceLockEnable) << 5 |
                       Convert.ToByte(Trailer.AGCMGCEnable) << 4 |
                       Convert.ToByte(Trailer.DetectedSignalEnable) << 3 |
                       Convert.ToByte(Trailer.SpectralInversionEnable) << 2 |
                       Convert.ToByte(Trailer.OverrangeEnable) << 1 |
                       Convert.ToByte(Trailer.SampleLossEnable) << 0);
        temp[index + 3] = b;

        b = (byte)(Convert.ToByte(Trailer.CalibratedTimeIndicator) << 3 |
                   Convert.ToByte(Trailer.ValidDataIndicator) << 2 |
                   Convert.ToByte(Trailer.ReferenceLockIndicator) << 1 |
                   Convert.ToByte(Trailer.AGCMGCIndicator) << 0);
        temp[index + 2] = b;

        b = (byte)(Convert.ToByte(Trailer.DetectedSignalIndicator) << 7 |
                   Convert.ToByte(Trailer.SpectralInversionIndicator) << 6 |
                   Convert.ToByte(Trailer.OverrangeIndicator) << 5 |
                   Convert.ToByte(Trailer.SampleLossIndicator) << 4);
        temp[index + 1] = b;

        b = (byte)(Convert.ToByte(Trailer.e) << 7 |
                   Trailer.AssociatedContextPacketCount);
        temp[index] = b;

        index += 4;

        return index;
    }

    /// <summary>
    /// Calculates the number of bytes needed for the preamble (header + stream_id + class_id + timestamps).
    /// </summary>
    protected int CalculatePreambleSize()
    {
        var numBytes = 4 + 4; // Header + StreamID
        if (Header.c) numBytes += 8;
        if (Header.tsi != VitaTimeStampIntegerType.None) numBytes += 4;
        if (Header.tsf != VitaTimeStampFractionalType.None) numBytes += 8;
        return numBytes;
    }

    /// <summary>
    /// Returns 4 if trailer is present, 0 otherwise.
    /// </summary>
    protected int TrailerSize()
    {
        return Header.t ? 4 : 0;
    }

}