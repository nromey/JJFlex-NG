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
using System.Diagnostics;

namespace Flex.Smoothlake.Vita
{
    /// <summary>
    /// Represents a single Vita IF Data Packet as defined in the Vita 49 Standard Section 6.1.
    /// Can also represent an Extended Data Packet as seen in Section 6.2.
    /// </summary>
    public class VitaOpusDataPacket : VitaPacketBase
    {
        public byte[] payload;

        public int Length;

        public VitaOpusDataPacket()
        {
            Header.pkt_type = VitaPacketType.IFDataWithStream;
            Header.c = true;
            Header.t = true;
            Header.tsi = VitaTimeStampIntegerType.Other;
            Header.tsf = VitaTimeStampFractionalType.RealTime;
        }

        public VitaOpusDataPacket(byte[] data, int bytes)
        {
            Length = bytes;

            int index = ParsePreamble(data);
            int payload_bytes = CalculatePayloadBytes();
            
            payload_bytes = bytes - 28;
            payload = new byte[payload_bytes];

            Buffer.BlockCopy(data, index, payload, 0, payload_bytes);
            //payload = data;S
            index += payload_bytes;

            ParseTrailer(data, index);
        }

        public byte[] ToBytesTX()
        {
            int num_bytes = CalculatePreambleSize() + payload.Length * sizeof(byte) + TrailerSize();

            byte[] temp = new byte[num_bytes];
            int index = WriteHeaderBytes(temp);

            // copy the payload
            //Buffer.BlockCopy(payload, 0, temp, index, payload.Length*sizeof(float));
            Buffer.BlockCopy(payload, 0, temp, index, payload.Length * sizeof(byte));

            index += payload.Length * sizeof(byte);

            WriteTrailerBytes(temp, index);

            return temp;
        }
        public byte[] ToBytes()
        {
            var numBytes = CalculatePreambleSize() + payload.Length * sizeof(byte) + TrailerSize();

            var temp = new byte[numBytes];
            var index = WriteHeaderBytes(temp);

            // copy the payload — opus data is opaque bytes, no endian swap needed
            Buffer.BlockCopy(payload, 0, temp, index, payload.Length * sizeof(byte));
            index += payload.Length * sizeof(byte);

            WriteTrailerBytes(temp, index);

            return temp;
        }
    }
}
