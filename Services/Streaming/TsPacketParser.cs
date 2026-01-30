using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ModernIPTVPlayer.Services.Streaming
{
    public class TsPacketParser
    {
        public const int PacketSize = 188;
        public const byte SyncByte = 0x47;

        public static readonly byte[] NullPacket = GenerateNullPacket();

        private static byte[] GenerateNullPacket()
        {
            byte[] p = new byte[PacketSize];
            p[0] = SyncByte;
            p[1] = 0x1F; // PID high (Null PID = 0x1FFF)
            p[2] = 0xFF; // PID low
            p[3] = 0x10; // CC=0, Payload only
            for (int i = 4; i < PacketSize; i++) p[i] = 0xFF; // Stuffing
            return p;
        }

        public struct TsHeader
        {
            public ushort Pid;
            public byte ContinuityCounter;
            public bool HasAdaptationField;
            public bool HasPayload;
            public bool PayloadUnitStartIndicator;
            public int AdaptationFieldLength;
            public long? Pcr;
        }

        /// <summary>
        /// Attempts to parse a single TS packet from a span of 188 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseHeader(ReadOnlySpan<byte> packet, out TsHeader header)
        {
            header = default;
            if (packet.Length < PacketSize || packet[0] != SyncByte) 
                return false;

            // Byte 1-2: [TEI (1)] [PUSI (1)] [Priority (1)] [PID (13)]
            header.PayloadUnitStartIndicator = (packet[1] & 0x40) != 0;
            header.Pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);

            // Byte 3: [Scrambling (2)] [Adaptation (2)] [Continuity (4)]
            byte adaptationFieldControl = (byte)((packet[3] & 0x30) >> 4);
            header.HasAdaptationField = (adaptationFieldControl & 0x02) != 0;
            header.HasPayload = (adaptationFieldControl & 0x01) != 0;
            header.ContinuityCounter = (byte)(packet[3] & 0x0F);

            // Extract PCR if Adaptation Field exists
            if (header.HasAdaptationField && packet.Length > 4)
            {
                header.AdaptationFieldLength = packet[4];
                if (header.AdaptationFieldLength > 0 && packet.Length > 11)
                {
                    byte flags = packet[5];
                    bool hasPcr = (flags & 0x10) != 0;
                    if (hasPcr && header.AdaptationFieldLength >= 7)
                    {
                        // PCR is 42 bits (33 base + 6 reserved + 9 extension)
                        long pcrBase = ((long)packet[6] << 25) |
                                       ((long)packet[7] << 17) |
                                       ((long)packet[8] << 9) |
                                       ((long)packet[9] << 1) |
                                       ((long)packet[10] >> 7);
                        header.Pcr = pcrBase;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Attempts to extract the PTS (Presentation Time Stamp) from a PES header.
        /// </summary>
        public static bool TryGetPts(ReadOnlySpan<byte> packet, TsHeader header, out long pts)
        {
            pts = 0;
            if (!header.PayloadUnitStartIndicator || !header.HasPayload) return false;

            // USE RAW OFFSET (PES Start), NOT Payload Offset
            int offset = GetPesStartOffset(packet, header);
            if (offset == -1 || offset + 13 >= packet.Length) return false;

            // Check PES Start Code (00 00 01)
            if (packet[offset] != 0 || packet[offset + 1] != 0 || packet[offset + 2] != 1) return false;

            // Check if we have enough bytes for flags
            if (offset + 7 >= packet.Length) return false;

            byte flags2 = packet[offset + 7];
            int ptsDtsIndicator = (flags2 >> 6) & 0x03;

            // Indicator '10' (2) means PTS only. '11' (3) means PTS + DTS.
            if (ptsDtsIndicator == 2 || ptsDtsIndicator == 3)
            {
                if (offset + 13 >= packet.Length) return false;

                long p0 = (packet[offset + 9] >> 1) & 0x07;       // Bits 32..30
                long p1 = packet[offset + 10];                    // Bits 29..22
                long p2 = (packet[offset + 11] >> 1); // Bits 21..15 (7 bits)
                long p3 = packet[offset + 12];                    // Bits 14..7
                long p4 = (packet[offset + 13] >> 1); // Bits 6..0  (7 bits)

                pts = (p0 << 30) | (p1 << 22) | (p2 << 15) | (p3 << 7) | p4;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the offset of the raw payload data (e.g. PES Header Start).
        /// Note: This does NOT skip the PES header.
        /// </summary>
        private static int GetPesStartOffset(ReadOnlySpan<byte> packet, TsHeader header)
        {
            int offset = 4;
            if (header.HasAdaptationField)
            {
                offset += 1 + packet[4];
            }
            return offset;
        }

        /// <summary>
        /// Calculates the offset where the actual Elementary Stream (ES) payload begins.
        /// Skips TS Header, Adaptation Field, AND PES Header (if present).
        /// </summary>
        public static int GetPayloadOffset(ReadOnlySpan<byte> packet, TsHeader header)
        {
            int offset = GetPesStartOffset(packet, header);

            // If this is a PES Start (PUSI), we must SKIP the PES Header to get to the ES Payload
            if (header.PayloadUnitStartIndicator)
            {
                if (offset + 3 < packet.Length &&
                    packet[offset] == 0x00 && packet[offset + 1] == 0x00 && packet[offset + 2] == 0x01)
                {
                    // PES Header Logic (Reverted from original)
                    // Byte 8 is PES_header_data_length
                    if (offset + 8 < packet.Length)
                    {
                        int pesHeaderDataLen = packet[offset + 8];
                        int totalPesHeaderLen = 9 + pesHeaderDataLen;
                        offset += totalPesHeaderLen;
                    }
                }
            }
            return offset;
        }

        /// <summary>
        /// Calculates the offset where the actual Elementary Stream (ES) payload begins.
        /// Skips TS Header, Adaptation Field, and PES Header (if present).
        /// </summary>
        private static int GetPayloadOffset_Original(ReadOnlySpan<byte> packet, TsHeader header)
        {
            int offset = 4;
            if (header.HasAdaptationField)
            {
                offset += 1 + packet[4];
            }

            if (offset >= packet.Length) return -1;

            // If PUSI is set, a PES Header is present at the start of the payload.
            // We MUST skip it to find the actual NAL units.
            if (header.PayloadUnitStartIndicator)
            {
                // PES Start Code Prefix: 00 00 01
                if (offset + 3 < packet.Length && 
                    packet[offset] == 0x00 && packet[offset+1] == 0x00 && packet[offset+2] == 0x01)
                {
                    // Byte 3 is Stream ID (e.g. 0xE0 for video)
                    // Byte 4-5 is PES Packet Length
                    // Byte 6-8 is Flags/Header Depth logic
                    
                    // We need at least 9 bytes to check the Optional Header length
                    if (offset + 8 < packet.Length)
                    {
                        // Byte 8 is PES_header_data_length
                        int pesHeaderDataLen = packet[offset + 8];
                        
                        // Total PES Header size = 6 (Fixed) + 3 (Flags/Len) + HeaderDataLen
                        int totalPesHeaderLen = 9 + pesHeaderDataLen;
                        
                        offset += totalPesHeaderLen;
                    }
                }
            }

            return offset;
        }

        /// <summary>
        /// Heuristic to detect if a TS packet contains the start of an I-Frame (IDR/Keyframe).
        /// </summary>
        public static bool IsIFrame(ReadOnlySpan<byte> packet, TsHeader header)
        {
            if (!header.PayloadUnitStartIndicator || !header.HasPayload) return false;

            int offset = GetPayloadOffset(packet, header);
            if (offset == -1 || offset >= packet.Length) return false;

            // Search for NAL unit start code 00 00 01 in the payload area
            // We search the ENTIRE payload to be robust against large SEI/Filler NALs before the IDR.
            for (int i = offset; i < packet.Length - 4; i++)
            {
                if (packet[i] == 0x00 && packet[i+1] == 0x00 && packet[i+2] == 0x01)
                {
                    byte nalHeader = packet[i+3];
                    
                    // HEVC (H.265): bits 1-6 are the type
                    int hevcType = (nalHeader >> 1) & 0x3F;
                    if (hevcType >= 16 && hevcType <= 23) return true; // BLA, IDR, CRA (Keyframes)
                    if (hevcType >= 32 && hevcType <= 34) return true; // VPS, SPS, PPS (Parameter sets)
                    // Note: AUD (35) is NOT a keyframe - it's just a delimiter. Don't treat as I-Frame.
                    
                    // AVC (H.264): bits 0-4 are the type
                    int avcType = nalHeader & 0x1F;
                    if (avcType == 5 || avcType == 7 || avcType == 8) return true; // IDR, SPS, PPS
                }
            }
            return false;
        }

        public static int GetHevcNalType(ReadOnlySpan<byte> packet, TsHeader header)
        {
            if (!header.HasPayload) return -1;

            int offset = GetPayloadOffset(packet, header);
            if (offset == -1 || offset >= packet.Length) return -1;

            // Robustly scan the entire available payload
            for (int i = offset; i < packet.Length - 4; i++)
            {
                if (packet[i] == 0x00 && packet[i+1] == 0x00 && packet[i+2] == 0x01)
                {
                    byte nalHeader = packet[i+3];
                    return (nalHeader >> 1) & 0x3F;
                }
            }
            return -1;
        }

        public static bool IsRaslFrame(ReadOnlySpan<byte> packet, TsHeader header)
        {
            int hevcType = GetHevcNalType(packet, header);
            return hevcType == 8 || hevcType == 9; // RASL_N, RASL_R
        }

        /// <summary>
        /// Finds the first sync byte in a buffer.
        /// </summary>
        public static int FindSyncByte(ReadOnlySpan<byte> buffer)
        {
            return buffer.IndexOf(SyncByte);
        }

        /// <summary>
        /// Finds the next NAL unit start code (0x000001 or 0x00000001).
        /// </summary>
        public static int FindNalUnitStart(ReadOnlySpan<byte> data, int startOffset, out int startCodeLen)
        {
            startCodeLen = 0;
            if (data == null) return -1;
            
            for (int i = startOffset; i < data.Length - 3; i++)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x00)
                {
                    if (data[i + 2] == 0x01)
                    {
                        startCodeLen = 3;
                        return i;
                    }
                    if (data[i + 2] == 0x00 && i + 3 < data.Length && data[i + 3] == 0x01)
                    {
                        startCodeLen = 4;
                        return i;
                    }
                }
            }
            return -1;
        }
    }
}
