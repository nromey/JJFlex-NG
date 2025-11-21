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
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Flex.Util;

namespace Flex.Smoothlake.Vita
{
    public class VitaDiscoveryPacket
    {
        public Header header;
        public uint stream_id;
        public VitaClassID class_id;
        public uint timestamp_int;
        public ulong timestamp_frac;
        public uint start_bin_index;
        public uint num_bins;
        public uint bin_size; // in bytes
        public uint frame_index;
        public string payload;
        public Trailer trailer;

        public VitaDiscoveryPacket()
        {
            header = new Header();
            header.pkt_type = VitaPacketType.ExtDataWithStream;
            header.c = true;
            header.t = true;
            header.tsi = VitaTimeStampIntegerType.None;
            header.tsf = VitaTimeStampFractionalType.None;
            trailer = new Trailer();
        }

        unsafe public VitaDiscoveryPacket(byte[] data, int bytes)
        {
            int index = 0;
            uint temp = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
            index += 4;

            header = new Header();
            header.pkt_type = (VitaPacketType)(temp >> 28);
            header.c = ((temp & 0x08000000) != 0);
            header.t = ((temp & 0x04000000) != 0);
            header.tsi = (VitaTimeStampIntegerType)((temp >> 22) & 0x03);
            header.tsf = (VitaTimeStampFractionalType)((temp >> 20) & 0x03);
            header.packet_count = (byte)((temp >> 16) & 0x0F);
            header.packet_size = (ushort)(temp & 0xFFFF);

            // if packet is a type with a stream id, read/save it
            if (header.pkt_type == VitaPacketType.IFDataWithStream ||
                header.pkt_type == VitaPacketType.ExtDataWithStream)
            {
                stream_id = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
                index += 4;
            }

            if (header.c)
            {
                temp = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
                index += 4;
                class_id.OUI = temp & 0x00FFFFFF;

                temp = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
                index += 4;
                class_id.InformationClassCode = (ushort)(temp >> 16);
                class_id.PacketClassCode = (ushort)temp;
            }

            if (header.tsi != VitaTimeStampIntegerType.None)
            {
                timestamp_int = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
                index += 4;
            }

            if (header.tsf != VitaTimeStampFractionalType.None)
            {
                timestamp_frac = ByteOrder.SwapBytes(BitConverter.ToUInt64(data, index));
                index += 8;
            }

            int payload_bytes = (header.packet_size - 1) * 4; // -1 for header
            switch (header.pkt_type)
            {
                case VitaPacketType.IFDataWithStream:
                case VitaPacketType.ExtDataWithStream:
                    payload_bytes -= 4;
                    break;
            }

            if (header.c) payload_bytes -= 8;
            if (header.tsi != VitaTimeStampIntegerType.None) payload_bytes -= 4;
            if (header.tsf != VitaTimeStampFractionalType.None) payload_bytes -= 8;
            if (header.t) payload_bytes -= 4;

            payload = Encoding.UTF8.GetString(data, index, payload_bytes);

            index += payload_bytes;

            if (header.t)
            {
                temp = ByteOrder.SwapBytes(BitConverter.ToUInt32(data, index));
                trailer.CalibratedTimeEnable = (temp & 0x80000000) != 0;
                trailer.ValidDataEnable = (temp & 0x40000000) != 0;
                trailer.ReferenceLockEnable = (temp & 0x20000000) != 0;
                trailer.AGCMGCEnable = (temp & 0x10000000) != 0;
                trailer.DetectedSignalEnable = (temp & 0x08000000) != 0;
                trailer.SpectralInversionEnable = (temp & 0x04000000) != 0;
                trailer.OverrangeEnable = (temp & 0x02000000) != 0;
                trailer.SampleLossEnable = (temp & 0x01000000) != 0;

                trailer.CalibratedTimeIndicator = (temp & 0x00080000) != 0;
                trailer.ValidDataIndicator = (temp & 0x00040000) != 0;
                trailer.ReferenceLockIndicator = (temp & 0x00020000) != 0;
                trailer.AGCMGCIndicator = (temp & 0x00010000) != 0;
                trailer.DetectedSignalIndicator = (temp & 0x00008000) != 0;
                trailer.SpectralInversionIndicator = (temp & 0x00004000) != 0;
                trailer.OverrangeIndicator = (temp & 0x00002000) != 0;
                trailer.SampleLossIndicator = (temp & 0x00001000) != 0;

                trailer.e = (temp & 0x80) != 0;
                trailer.AssociatedContextPacketCount = (byte)(temp & 0xEF);
            }
        }        
    }
}
