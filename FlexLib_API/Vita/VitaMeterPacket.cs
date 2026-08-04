// ****************************************************************************
///*!	\file VitaMeterPacket.cs
// *	\brief Defines a Vita Extended Data Packet for Meters
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

namespace Flex.Smoothlake.Vita
{
    public class VitaMeterPacket : VitaPacketBase
    {
        private ushort[] ids;
        private short[] vals;

        public VitaMeterPacket(byte[] data)
        {
            int index = ParsePreamble(data);
            int payload_bytes = CalculatePayloadBytes();

            Debug.Assert(payload_bytes % 4 == 0);

            int num_meters = payload_bytes / 4;

            ids = new ushort[num_meters];
            vals = new short[num_meters];

            for (int i = 0; i < num_meters; i++)
            {
                ids[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index));
                index += 2;

                vals[i] = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(index));
                index += 2;
            }

            ParseTrailer(data, index);
        }

        public int NumMeters
        {
            get { return ids.Length; }
        }

        public ushort GetMeterID(int index)
        {
            return ids[index];
        }

        public short GetMeterValue(int index)
        {
            return vals[index];
        }
    }   
}
