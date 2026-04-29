// ****************************************************************************
///*!	\file VitaDiscovery.cs
// *	\brief Defines a Vita Extended Data Packet for FFTs
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2014-02-19
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Text;

using Flex.Util;

namespace Flex.Smoothlake.Vita
{
    public class VitaDiscoveryPacket : VitaPacketBase
    {
        private string _payload;
        public string payload
        {
            get { return _payload; }
            set
            {
                if (_payload != value)
                {
                    _payload = value;

                    // ensure that the payload is VITA compliant (even 32-bit word boundary)
                    int payload_bytes = Encoding.UTF8.GetByteCount(_payload);
                    if (payload_bytes % 4 != 0)
                    {
                        int bytes_to_add = 4 - (payload_bytes % 4);
                        _payload = _payload.PadRight(_payload.Length + bytes_to_add, ' ');
                    }

                    UpdateHeaderPacketSize();
                }
            }
        }

        private void UpdateHeaderPacketSize()
        {
            ushort packet_size = 1; // for header
            if (Header.c) packet_size += 2;
            if (Header.t) packet_size += 1;
            if (Header.tsi != VitaTimeStampIntegerType.None) packet_size += 1;
            if (Header.tsf != VitaTimeStampFractionalType.None) packet_size += 2;

            switch (Header.pkt_type)
            {
                case VitaPacketType.IFDataWithStream:
                case VitaPacketType.ExtDataWithStream:
                    packet_size += 1;
                    break;
            }

            int payload_bytes = Encoding.UTF8.GetByteCount(_payload);
            ushort payload_words = (ushort)(payload_bytes / 4);
            if (payload_words * 4 != payload_bytes)
                payload_words++;

            packet_size += payload_words;

            Header.packet_size = packet_size;
        }

        public VitaDiscoveryPacket()
        {
            Header.pkt_type = VitaPacketType.ExtDataWithStream;
            Header.c = true;
            Header.t = true;
            Header.tsi = VitaTimeStampIntegerType.None;
            Header.tsf = VitaTimeStampFractionalType.None;
        }

        public VitaDiscoveryPacket(byte[] data, int bytes)
        {
            int index = ParsePreamble(data);
            int payload_bytes = CalculatePayloadBytes();

            payload = Encoding.UTF8.GetString(data, index, payload_bytes);

            index += payload_bytes;

            ParseTrailer(data, index);
        }

        public byte[] ToBytes()
        {
            int payload_bytes = Encoding.UTF8.GetByteCount(_payload);
            int num_bytes = CalculatePreambleSize() + payload_bytes + TrailerSize();

            byte[] temp = new byte[num_bytes];
            int index = WriteHeaderBytes(temp);

            // copy the payload
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(_payload), 0, temp, index, payload_bytes);
            index += payload_bytes;

            WriteTrailerBytes(temp, index);

            return temp;
        }
    }
}
