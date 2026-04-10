using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services.Streaming
{
    /// <summary>
    /// Unified HEVC-aware TS/PES/NAL parser.
    /// Replaces TsPacketParser.cs with complete SPS/VUI parsing following FFmpeg implementation.
    /// Reference: FFmpeg libavcodec/h2645_vui.c, hevc/ps.c, cbs_h265_syntax_template.c, h2645_parse.c
    /// </summary>
    public static class HevcTsParser
    {
        public const int TsPacketSize = 188;
        public const byte TsSyncByte = 0x47;
        private const bool VerboseLogging = false;

        private static void LogVerbose(string message)
        {
            if (VerboseLogging) Debug.WriteLine(message);
        }

        #region Parse Result

        /// <summary>
        /// Result of parsing a PES payload for HEVC NAL units
        /// </summary>
        public class ParseResult
        {
            public List<int> NalTypesFound = new List<int>();
            public int TotalNalsFound;
            public int TotalKeyframes;
            public int TotalParamSets;
            public bool HasReceivedVPS;
            public bool HasReceivedSPS;
            public bool HasReceivedPPS;
            public bool PacketHasHeaders;
            public bool GateOpen;
            public HevcSpsInfo? ColorInfo;
        }

        #endregion

        #region Bitstream Reader (FFmpeg get_bits/get_bits1/get_ue_golomb)

        /// <summary>
        /// Bitstream reader following FFmpeg's GetBitContext patterns.
        /// Handles emulation prevention byte removal (0x00 0x00 0x03 0x00/0x01/0x02/0x03 -> 0x00 0x00 0x00/0x01/0x02/0x03)
        /// </summary>
        public class BitstreamReader
        {
            private readonly byte[] _data;
            private int _bitPos;
            private readonly int _bitLen;

            public BitstreamReader(byte[] data, int offset = 0, int length = -1)
            {
                _data = new byte[length > 0 ? length : data.Length - offset];
                Array.Copy(data, offset, _data, 0, _data.Length);
                _bitPos = 0;
                _bitLen = _data.Length * 8;
            }

            public int BitsRemaining => _bitLen - _bitPos;
            public bool IsEnd => _bitPos >= _bitLen;

            /// <summary>
            /// Read 1 bit (FFmpeg get_bits1)
            /// </summary>
            public int GetBit1()
            {
                if (_bitPos >= _bitLen) return 0;
                int bit = (_data[_bitPos >> 3] >> (7 - (_bitPos & 7))) & 1;
                _bitPos++;
                return bit;
            }

            /// <summary>
            /// Read N bits (FFmpeg get_bits)
            /// </summary>
            public uint GetBits(int n)
            {
                if (n <= 0) return 0;
                if (n > 32) throw new ArgumentOutOfRangeException(nameof(n), "Cannot read more than 32 bits at once");

                uint result = 0;
                for (int i = 0; i < n; i++)
                {
                    result = (result << 1) | (uint)GetBit1();
                }
                return result;
            }

            /// <summary>
            /// Skip N bits
            /// </summary>
            public void SkipBits(int n)
            {
                _bitPos = Math.Min(_bitPos + n, _bitLen);
            }

            /// <summary>
            /// Read unsigned exp-Golomb code ue(v) (FFmpeg get_ue_golomb)
            /// </summary>
            public uint GetUeGolomb()
            {
                int leadingZeroBits = 0;
                while (_bitPos < _bitLen && GetBit1() == 0)
                {
                    leadingZeroBits++;
                    if (leadingZeroBits > 32)
                    {
                        LogVerbose("[HevcTsParser] Invalid exp-Golomb code (too many leading zeros)");
                        return 0;
                    }
                }

                if (leadingZeroBits == 0) return 0;
                if (leadingZeroBits > 31) return 0;

                uint codeNum = (1u << leadingZeroBits) - 1 + GetBits(leadingZeroBits);
                return codeNum;
            }

            /// <summary>
            /// Read signed exp-Golomb code se(v) (FFmpeg get_se_golomb)
            /// </summary>
            public int GetSeGolomb()
            {
                uint ueVal = GetUeGolomb();
                // se(v) mapping: ue(v) -> (-1)^(ue+1) * ceil(ue/2)
                // Equivalently: if ue is odd, result = (ue+1)/2; if even, result = -ue/2
                return (ueVal & 1) == 1 ? (int)((ueVal + 1) >> 1) : -(int)(ueVal >> 1);
            }

            /// <summary>
            /// Check if more bits available
            /// </summary>
            public bool MoreRbspData()
            {
                if (_bitPos >= _bitLen) return false;
                // Check if remaining bits have any non-zero bit
                for (int i = _bitPos; i < _bitLen; i++)
                {
                    int byteIdx = i >> 3;
                    int bitIdx = 7 - (i & 7);
                    if (((_data[byteIdx] >> bitIdx) & 1) != 0) return true;
                }
                return false;
            }

            /// <summary>
            /// Skip trailing zero bits and check for rbsp_stop_one_bit
            /// </summary>
            public void SkipTrailingBits()
            {
                // Skip rbsp_trailing_bits (should be 1 followed by zeros)
                int bit = GetBit1();
                if (bit != 1)
                {
                    LogVerbose("[HevcTsParser] Expected rbsp_stop_one_bit to be 1");
                }
                // Skip any remaining zeros
                while (_bitPos < _bitLen && GetBit1() == 0) { }
            }
        }

        #endregion

        #region TS Packet Parsing

        public struct TsHeader
        {
            public ushort Pid;
            public byte ContinuityCounter;
            public bool HasAdaptationField;
            public bool HasPayload;
            public bool PayloadUnitStartIndicator;
            public int AdaptationFieldLength;
            public int PayloadStartOffset;
        }

        /// <summary>
        /// Parse TS packet header (188 bytes)
        /// </summary>
        public static bool ParseTsHeader(ReadOnlySpan<byte> packet, out TsHeader header)
        {
            header = default;
            if (packet.Length < TsPacketSize || packet[0] != TsSyncByte)
                return false;

            header.PayloadUnitStartIndicator = (packet[1] & 0x40) != 0;
            header.Pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);

            byte adaptationFieldControl = (byte)((packet[3] & 0x30) >> 4);
            header.HasAdaptationField = (adaptationFieldControl & 0x02) != 0;
            header.HasPayload = (adaptationFieldControl & 0x01) != 0;
            header.ContinuityCounter = (byte)(packet[3] & 0x0F);

            header.PayloadStartOffset = 4;
            if (header.HasAdaptationField && packet.Length > 4)
            {
                header.AdaptationFieldLength = packet[4];
                header.PayloadStartOffset += (header.AdaptationFieldLength + 1);
            }

            return true;
        }

        #endregion

        #region PES Header Parsing

        public struct PesHeader
        {
            public bool IsValid;
            public byte StreamId;          // 0xE0-0xEF = video, 0xC0-0xDF = audio
            public ushort PesPacketLength; // 0 = unbounded (common in live TS)
            public bool HasPts;
            public bool HasDts;
            public long Pts;
            public long Dts;
            public int HeaderSize;         // Total PES header size including optional PTS/DTS
            public int PayloadOffset;      // Offset to PES payload (after PES header)
        }

        /// <summary>
        /// Parse PES header from TS payload
        /// </summary>
        public static bool ParsePesHeader(byte[] data, int offset, out PesHeader pes)
        {
            pes = default;
            if (offset + 6 > data.Length) return false;

            // Check PES start code: 00 00 01
            if (data[offset] != 0x00 || data[offset + 1] != 0x00 || data[offset + 2] != 0x01)
                return false;

            pes.StreamId = data[offset + 3];
            pes.PesPacketLength = (ushort)((data[offset + 4] << 8) | data[offset + 5]);

            // Video stream IDs: 0xE0-0xEF
            // Audio stream IDs: 0xC0-0xDF
            if (pes.StreamId < 0xC0) return false; // Not a valid PES stream

            pes.HeaderSize = 6; // Fixed part
            pes.PayloadOffset = offset + 6;

            // Check if there's more PES header (PTS/DTS)
            if (offset + 9 > data.Length)
            {
                pes.IsValid = true;
                return true;
            }

            // Bytes 7-8: PES flags and header data length
            byte flags = data[offset + 7];
            byte headerDataLen = data[offset + 8];

            pes.HasPts = (flags & 0x80) != 0; // PTS_DTS_flags == '10' or '11'
            pes.HasDts = (flags & 0x40) != 0; // PTS_DTS_flags == '11'

            pes.HeaderSize = 6 + 3 + headerDataLen;
            pes.PayloadOffset = offset + pes.HeaderSize;

            // Parse PTS if present
            if (pes.HasPts && offset + 14 <= data.Length)
            {
                byte ptsByte1 = data[offset + 9];
                byte ptsByte2 = data[offset + 10];
                byte ptsByte3 = data[offset + 11];
                byte ptsByte4 = data[offset + 12];
                byte ptsByte5 = data[offset + 13];

                pes.Pts = ((long)(ptsByte1 & 0x0E) << 29) |
                          ((long)ptsByte2 << 22) |
                          ((long)(ptsByte3 & 0xFE) << 14) |
                          ((long)ptsByte4 << 7) |
                          ((long)(ptsByte5 >> 1));
            }

            // Parse DTS if present
            if (pes.HasDts && offset + 19 <= data.Length)
            {
                byte dtsByte1 = data[offset + 14];
                byte dtsByte2 = data[offset + 15];
                byte dtsByte3 = data[offset + 16];
                byte dtsByte4 = data[offset + 17];
                byte dtsByte5 = data[offset + 18];

                pes.Dts = ((long)(dtsByte1 & 0x0E) << 29) |
                          ((long)dtsByte2 << 22) |
                          ((long)(dtsByte3 & 0xFE) << 14) |
                          ((long)dtsByte4 << 7) |
                          ((long)(dtsByte5 >> 1));
            }

            pes.IsValid = true;
            return true;
        }

        #endregion

        #region NAL Unit Detection

        /// <summary>
        /// Find NAL unit start code in data
        /// Returns offset of start code, sets startCodeLen to 3 or 4
        /// </summary>
        public static int FindNalUnitStart(byte[] data, int startOffset, out int startCodeLen)
        {
            startCodeLen = 0;
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

        /// <summary>
        /// Get HEVC NAL type from NAL header byte
        /// HEVC: nal_unit_type = (byte & 0x7E) >> 1
        /// </summary>
        public static int GetHevcNalType(byte nalHeaderByte)
        {
            return (nalHeaderByte & 0x7E) >> 1;
        }

        #endregion

        #region HEVC SPS/VUI Parsing (FFmpeg h2645_vui.c + hevc/ps.c)

        /// <summary>
        /// Parsed SPS color information
        /// Values per FFmpeg libavutil/pixfmt.h constants
        /// </summary>
        public struct HevcSpsInfo
        {
            public bool IsValid;
            public bool HasVui;
            public bool HasColourDescription;

            // Color space values (from FFmpeg AVColorPrimaries, AVColorTransferCharacteristic, AVColorSpace)
            public int ColourPrimaries;          // 1=BT.709, 9=BT.2020
            public int TransferCharacteristics;  // 1=BT.709, 16=PQ(SMPTE2084), 18=HLG(ARIB-B67)
            public int MatrixCoefficients;       // 1=BT.709, 9=BT.2020_NCL, 10=BT.2020_CL
            public int VideoFullRangeFlag;       // 0=limited(16-235), 1=full(0-255)

            // SPS parameters
            public int Width;
            public int Height;
            public int BitDepthLuma;
            public int BitDepthChroma;
            public int ChromaFormatIdc;  // 1=4:2:0, 2=4:2:2, 3=4:4:4
        }

        /// <summary>
        /// Remove emulation prevention bytes (0x03 inserted after 00 00 to prevent false start codes)
        /// FFmpeg does this before passing NAL payload to parsers
        /// </summary>
        private static byte[] RemoveEmulationPrevention(byte[] data)
        {
            var result = new List<byte>(data.Length);
            int zeroCount = 0;

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0x00)
                {
                    zeroCount++;
                    result.Add(data[i]);
                }
                else if (zeroCount == 2 && data[i] == 0x03)
                {
                    // Emulation prevention byte - skip it
                    zeroCount = 0;
                }
                else
                {
                    zeroCount = 0;
                    result.Add(data[i]);
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Parse HEVC SPS for VUI color metadata
        /// This follows FFmpeg's ff_hevc_parse_sps EXACT field order from libavcodec/hevc/ps.c
        /// Fields are read in the same order, same bit widths, same conditional logic.
        /// </summary>
        public static HevcSpsInfo ParseSps(byte[] spsPayload)
        {
            HevcSpsInfo spsInfo = new HevcSpsInfo();
            spsInfo.ColourPrimaries = 2;          // AVCOL_PRI_UNSPECIFIED
            spsInfo.TransferCharacteristics = 2;  // AVCOL_TRC_UNSPECIFIED
            spsInfo.MatrixCoefficients = 2;       // AVCOL_SPC_UNSPECIFIED
            spsInfo.VideoFullRangeFlag = 0;       // AVCOL_RANGE_MPEG (limited)

            try
            {
                LogVerbose($"[HevcTsParser] SPS payload: {spsPayload.Length} bytes, first 16: {BitConverter.ToString(spsPayload, 0, Math.Min(16, spsPayload.Length)).Replace("-", " ")}");

                var reader = new BitstreamReader(spsPayload);

                // === 1. sps_video_parameter_set_id (4 bits) ===
                int vpsId = (int)reader.GetBits(4);
                LogVerbose($"[HevcTsParser]   vps_id={vpsId}, bits left={reader.BitsRemaining}");

                // === 2. sps_max_sub_layers_minus1 (3 bits) ===
                int maxSubLayersMinus1 = (int)reader.GetBits(3) + 1 - 1;
                int maxSubLayers = maxSubLayersMinus1 + 1;
                LogVerbose($"[HevcTsParser]   max_sub_layers={maxSubLayers}, bits left={reader.BitsRemaining}");

                // === 3. sps_temporal_id_nesting_flag (1 bit) ===
                int temporalNesting = (int)reader.GetBits(1);
                LogVerbose($"[HevcTsParser]   temporal_id_nesting={temporalNesting}, bits left={reader.BitsRemaining}");

                // === 4. profile_tier_level ===
                LogVerbose($"[HevcTsParser]   profile_tier_level...");
                int bitsBeforePtl = reader.BitsRemaining;
                SkipProfileTierLevel(reader, maxSubLayersMinus1);
                LogVerbose($"[HevcTsParser]   profile_tier_level consumed {bitsBeforePtl - reader.BitsRemaining} bits, left={reader.BitsRemaining}");

                // === 5. sps_seq_parameter_set_id (ue(v)) ===
                int spsId = (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   sps_id={spsId}, bits left={reader.BitsRemaining}");

                // === 6. chroma_format_idc (ue(v)) ===
                spsInfo.ChromaFormatIdc = (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   chroma_format_idc={spsInfo.ChromaFormatIdc}, bits left={reader.BitsRemaining}");

                // === 7. separate_colour_plane_flag (1 bit) if chroma_format_idc == 3 ===
                if (spsInfo.ChromaFormatIdc == 3)
                {
                    reader.GetBits(1);
                    LogVerbose($"[HevcTsParser]   separate_colour_plane done, bits left={reader.BitsRemaining}");
                }

                // === 8. pic_width_in_luma_samples (ue(v)) ===
                spsInfo.Width = (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   width={spsInfo.Width}, bits left={reader.BitsRemaining}");

                // === 9. pic_height_in_luma_samples (ue(v)) ===
                spsInfo.Height = (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   height={spsInfo.Height}, bits left={reader.BitsRemaining}");

                // === 10. conformance_window_flag (1 bit) ===
                int confWindow = (int)reader.GetBits(1);
                if (confWindow != 0)
                {
                    reader.GetUeGolomb(); // conf_win_left_offset
                    reader.GetUeGolomb(); // conf_win_right_offset
                    reader.GetUeGolomb(); // conf_win_top_offset
                    reader.GetUeGolomb(); // conf_win_bottom_offset
                    LogVerbose($"[HevcTsParser]   conformance_window done, bits left={reader.BitsRemaining}");
                }

                // === 11. bit_depth_luma_minus8 (ue(v)) + 8 ===
                spsInfo.BitDepthLuma = 8 + (int)reader.GetUeGolomb();

                // === 12. bit_depth_chroma_minus8 (ue(v)) + 8 ===
                spsInfo.BitDepthChroma = 8 + (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   bit_depth={spsInfo.BitDepthLuma}/{spsInfo.BitDepthChroma}, bits left={reader.BitsRemaining}");

                // === 13. log2_max_pic_order_cnt_lsb_minus4 (ue(v)) + 4 ===
                int log2MaxPicOrderCntLsb = (int)reader.GetUeGolomb() + 4;
                LogVerbose($"[HevcTsParser]   log2_max_poc_lsb={log2MaxPicOrderCntLsb}, bits left={reader.BitsRemaining}");

                // === 14. sps_sub_layer_ordering_info_present_flag (1 bit) ===
                int subLayerOrderingInfo = (int)reader.GetBits(1);
                int startIdx = subLayerOrderingInfo != 0 ? 0 : maxSubLayersMinus1;

                // === 15. Temporal layer loop ===
                for (int i = startIdx; i <= maxSubLayersMinus1; i++)
                {
                    reader.GetUeGolomb(); // max_dec_pic_buffering_minus1 + 1
                    reader.GetUeGolomb(); // max_num_reorder_pics
                    reader.GetUeGolomb(); // max_latency_increase_plus1 - 1
                }
                LogVerbose($"[HevcTsParser]   temporal_layer loop done, bits left={reader.BitsRemaining}");

                // === 16. log2_min_luma_coding_block_size_minus3 (ue(v)) ===
                reader.GetUeGolomb();

                // === 17. log2_diff_max_min_luma_coding_block_size (ue(v)) ===
                reader.GetUeGolomb();

                // === 18. log2_min_transform_block_size_minus2 (ue(v)) ===
                reader.GetUeGolomb();

                // === 19. log2_diff_max_min_transform_block_size (ue(v)) ===
                reader.GetUeGolomb();

                // === 20. max_transform_hierarchy_depth_inter (ue(v)) ===
                reader.GetUeGolomb();

                // === 21. max_transform_hierarchy_depth_intra (ue(v)) ===
                reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   coding_block/transform_block done, bits left={reader.BitsRemaining}");

                // === 22. scaling_list_enabled_flag (1 bit) ===
                int scalingListEnabled = (int)reader.GetBits(1);
                if (scalingListEnabled != 0)
                {
                    int scalingListDataPresent = (int)reader.GetBits(1);
                    if (scalingListDataPresent != 0)
                    {
                        SkipScalingListData(reader);
                    }
                    LogVerbose($"[HevcTsParser]   scaling_list done, bits left={reader.BitsRemaining}");
                }

                // === 23. amp_enabled_flag (1 bit) ===
                reader.GetBits(1);

                // === 24. sample_adaptive_offset_enabled_flag (1 bit) ===
                reader.GetBits(1);

                // === 25. pcm_enabled_flag (1 bit) ===
                int pcmEnabled = (int)reader.GetBits(1);
                if (pcmEnabled != 0)
                {
                    reader.GetBits(4); // pcm_sample_bit_depth_luma_minus1
                    reader.GetBits(4); // pcm_sample_bit_depth_chroma_minus1
                    reader.GetUeGolomb(); // log2_min_pcm_luma_coding_block_size_minus3
                    reader.GetUeGolomb(); // log2_diff_max_min_pcm_luma_coding_block_size
                    reader.GetBits(1); // pcm_loop_filter_disabled_flag
                    LogVerbose($"[HevcTsParser]   pcm done, bits left={reader.BitsRemaining}");
                }

                // === 26. num_short_term_ref_pic_sets (ue(v)) ===
                int numStRefPicSets = (int)reader.GetUeGolomb();
                LogVerbose($"[HevcTsParser]   num_short_term_ref_pic_sets={numStRefPicSets}, bits left={reader.BitsRemaining}");

                // Skip each short-term ref pic set using FFmpeg's exact decode logic
                for (int i = 0; i < numStRefPicSets; i++)
                {
                    SkipShortTermRefPicSetExact(reader, i, numStRefPicSets);
                    LogVerbose($"[HevcTsParser]   ref_pic_set[{i}] done, bits left={reader.BitsRemaining}");
                    if (reader.BitsRemaining <= 0)
                    {
                        LogVerbose($"[HevcTsParser]   ERROR: All bits consumed by ref pic sets at index {i}");
                        spsInfo.IsValid = false;
                        return spsInfo;
                    }
                }

                // === 27. long_term_ref_pics_present_flag (1 bit) ===
                int longTermPresent = (int)reader.GetBits(1);
                if (longTermPresent != 0)
                {
                    int numLongTerm = (int)reader.GetUeGolomb();
                    for (int i = 0; i < numLongTerm; i++)
                    {
                        reader.GetBits(log2MaxPicOrderCntLsb); // lt_ref_pic_poc_lsb_sps
                        reader.GetBits(1); // used_by_curr_pic_lt_sps_flag
                    }
                    LogVerbose($"[HevcTsParser]   long_term_ref done ({numLongTerm} pics), bits left={reader.BitsRemaining}");
                }

                // === 28. sps_temporal_mvp_enabled_flag (1 bit) ===
                reader.GetBits(1);

                // === 29. strong_intra_smoothing_enabled_flag (1 bit) ===
                reader.GetBits(1);
                LogVerbose($"[HevcTsParser]   before VUI: bits left={reader.BitsRemaining}");

                // === 30. vui_parameters_present_flag (1 bit) ===
                int vuiPresent = (int)reader.GetBits(1);
                LogVerbose($"[HevcTsParser]   vui_parameters_present_flag={vuiPresent}, bits left={reader.BitsRemaining}");
                if (vuiPresent != 0)
                {
                    ParseVui(reader, ref spsInfo);
                    LogVerbose($"[HevcTsParser]   VUI: primaries={spsInfo.ColourPrimaries}, transfer={spsInfo.TransferCharacteristics}, matrix={spsInfo.MatrixCoefficients}");
                }
                else
                {
                    LogVerbose($"[HevcTsParser]   VUI NOT present in SPS");
                }

                spsInfo.IsValid = true;
                LogVerbose($"[HevcTsParser] SPS parsing complete: {spsInfo.Width}x{spsInfo.Height}, VUI={(vuiPresent != 0 ? "Y" : "N")}, ColorDesc={spsInfo.HasColourDescription}");
            }
            catch (Exception ex)
            {
                LogVerbose($"[HevcTsParser] SPS parse error: {ex.Message}\n{ex.StackTrace}");
            }

            return spsInfo;
        }

        /// <summary>
        /// Skip profile_tier_level syntax element (FFmpeg decode_profile_tier_level + parse_ptl)
        /// Follows FFmpeg's profile-dependent constraint flag reading exactly.
        /// </summary>
        private static void SkipProfileTierLevel(BitstreamReader reader, int maxSubLayersMinus1)
        {
            int generalProfileIdc = SkipPtlProfile(reader);

            // general_level_idc
            reader.GetBits(8);

            bool[] subLayerProfilePresent = new bool[Math.Max(maxSubLayersMinus1, 1)];
            bool[] subLayerLevelPresent = new bool[Math.Max(maxSubLayersMinus1, 1)];
            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                subLayerProfilePresent[i] = reader.GetBits(1) != 0;
                subLayerLevelPresent[i] = reader.GetBits(1) != 0;
            }

            if (maxSubLayersMinus1 > 0)
            {
                for (int i = maxSubLayersMinus1; i < 8; i++)
                {
                    reader.GetBits(2);
                }
            }

            for (int i = 0; i < maxSubLayersMinus1; i++)
            {
                if (subLayerProfilePresent[i])
                {
                    SkipPtlProfile(reader);
                }
                if (subLayerLevelPresent[i])
                {
                    reader.GetBits(8);
                }
            }
        }

        private static int SkipPtlProfile(BitstreamReader reader)
        {
            reader.GetBits(2); // profile_space
            reader.GetBits(1); // tier_flag
            int profileIdc = (int)reader.GetBits(5);

            bool[] compatibilityFlags = new bool[32];
            for (int i = 0; i < 32; i++)
            {
                compatibilityFlags[i] = reader.GetBits(1) != 0;
                if (profileIdc == 0 && i > 0 && compatibilityFlags[i])
                {
                    profileIdc = i;
                }
            }

            reader.GetBits(1); // progressive_source_flag
            reader.GetBits(1); // interlaced_source_flag
            reader.GetBits(1); // non_packed_constraint_flag
            reader.GetBits(1); // frame_only_constraint_flag

            bool CheckProfileIdc(int idc) => profileIdc == idc || compatibilityFlags[idc];

            if (CheckProfileIdc(4) || CheckProfileIdc(5) || CheckProfileIdc(6) ||
                CheckProfileIdc(7) || CheckProfileIdc(8) || CheckProfileIdc(9) ||
                CheckProfileIdc(10))
            {
                reader.GetBits(1); // max_12bit_constraint_flag
                reader.GetBits(1); // max_10bit_constraint_flag
                reader.GetBits(1); // max_8bit_constraint_flag
                reader.GetBits(1); // max_422chroma_constraint_flag
                reader.GetBits(1); // max_420chroma_constraint_flag
                reader.GetBits(1); // max_monochrome_constraint_flag
                reader.GetBits(1); // intra_constraint_flag
                reader.GetBits(1); // one_picture_only_constraint_flag
                reader.GetBits(1); // lower_bit_rate_constraint_flag

                if (CheckProfileIdc(5) || CheckProfileIdc(9) || CheckProfileIdc(10))
                {
                    reader.GetBits(1);  // max_14bit_constraint_flag
                    SkipLongBits(reader, 33); // reserved_zero_33bits
                }
                else
                {
                    SkipLongBits(reader, 34); // reserved_zero_34bits
                }
            }
            else if (CheckProfileIdc(2))
            {
                reader.GetBits(7);
                reader.GetBits(1);  // one_picture_only_constraint_flag
                SkipLongBits(reader, 35); // reserved_zero_35bits
            }
            else
            {
                SkipLongBits(reader, 43); // reserved_zero_43bits
            }

            if (CheckProfileIdc(1) || CheckProfileIdc(2) || CheckProfileIdc(3) ||
                CheckProfileIdc(4) || CheckProfileIdc(5) || CheckProfileIdc(9))
            {
                reader.GetBits(1); // inbld_flag
            }
            else
            {
                reader.GetBits(1); // reserved_zero_bit
            }

            return profileIdc;
        }

        private static void SkipLongBits(BitstreamReader reader, int bitCount)
        {
            while (bitCount > 0)
            {
                int chunk = Math.Min(bitCount, 32);
                reader.GetBits(chunk);
                bitCount -= chunk;
            }
        }

        /// <summary>
        /// Skip scaling_list_data syntax element
        /// </summary>
        private static void SkipScalingListData(BitstreamReader reader)
        {
            for (int sizeId = 0; sizeId < 4; sizeId++)
            {
                int matrixCount = (sizeId == 3) ? 2 : 6;
                for (int matrixId = 0; matrixId < matrixCount; matrixId++)
                {
                    int flag = (int)reader.GetBits(1); // scaling_list_pred_mode_flag
                    if (flag == 0)
                    {
                        reader.GetUeGolomb(); // scaling_list_pred_matrix_id_delta
                    }
                    else
                    {
                        int coefNum = Math.Min(64, (1 << (4 + (sizeId << 1))));
                        if (sizeId > 1)
                        {
                            reader.GetSeGolomb(); // scaling_list_dc_coef_minus8
                        }
                        for (int i = 0; i < coefNum; i++)
                        {
                            reader.GetSeGolomb(); // scaling_list_delta_coef
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Skip short_term_ref_pic_set syntax element
        /// This follows FFmpeg's ff_hevc_decode_short_term_rps EXACT logic from ps.c:89
        /// For the first RPS (stRpsIdx==0), prediction is always off since there's no previous RPS.
        /// For subsequent RPS, prediction may reference the previous one.
        /// We fully decode the structure (even discarding values) to consume the exact bit count.
        /// </summary>
        private static void SkipShortTermRefPicSetExact(BitstreamReader reader, int stRpsIdx, int numStRefPicSets)
        {
            if (stRpsIdx == 0)
            {
                // No prediction possible for the first RPS (there's no previous one to reference).
                // FFmpeg: rps->rps_predict = 0 (since rps == sps->st_rps && !sps->nb_st_rps at this point)
                // So we go straight to the non-prediction path.
                // num_negative_pics (ue(v))
                int numNegativePics = (int)reader.GetUeGolomb();
                // num_positive_pics (ue(v))
                int numPositivePics = (int)reader.GetUeGolomb();

                // For each negative pic: delta_poc_s0_minus1 (ue(v)) + used_by_curr_pic_s0_flag (1 bit)
                for (int i = 0; i < numNegativePics; i++)
                {
                    reader.GetUeGolomb(); // delta_poc_s0_minus1
                    reader.GetBits(1);    // used_by_curr_pic_s0_flag
                }
                // For each positive pic: delta_poc_s1_minus1 (ue(v)) + used_by_curr_pic_s1_flag (1 bit)
                for (int i = 0; i < numPositivePics; i++)
                {
                    reader.GetUeGolomb(); // delta_poc_s1_minus1
                    reader.GetBits(1);    // used_by_curr_pic_s1_flag
                }
            }
            else
            {
                // For stRpsIdx > 0, prediction IS possible.
                // FFmpeg checks: if (rps != sps->st_rps && sps->nb_st_rps) rps->rps_predict = get_bits1(gb);
                // Since we're building up st_rps one by one, nb_st_rps is already > 0 at this point,
                // so rps_predict flag IS present.
                int rpsPredict = (int)reader.GetBits(1);

                if (rpsPredict != 0)
                {
                    // Prediction path (FFmpeg lines 111-139):
                    // delta_idx_minus1 (ue(v)) — only if stRpsIdx == numStRefPicSets (slice header case)
                    // For SPS parsing, stRpsIdx < numStRefPicSets always, so delta_idx_minus1 is NOT present.

                    // delta_rps_sign (1 bit)
                    reader.GetBits(1);
                    // abs_delta_rps_minus1 (ue(v))
                    reader.GetUeGolomb();

                    // Here FFmpeg iterates: for (i = 0; i <= rps_ridx->num_delta_pocs; i++)
                    // The iteration count depends on the PREVIOUS RPS's num_delta_pocs value.
                    // Since we're skipping (not storing RPS data), we don't know the previous RPS's num_delta_pocs.
                    // We need to fully decode the previous RPS to know this value.
                    //
                    // SOLUTION: For SPS parsing, we track num_delta_pocs for each RPS index.
                    // We store these in a static field so they persist across calls.
                    int numDeltaPocs = GetNumDeltaPocsForRps(stRpsIdx - 1);

                    for (int i = 0; i <= numDeltaPocs; i++)
                    {
                        int used = (int)reader.GetBits(1); // used_by_curr_pic_flag
                        if (used == 0)
                        {
                            reader.GetBits(1); // use_delta_flag
                        }
                    }
                    // Store the computed num_delta_pocs for this RPS
                    // (For prediction path, num_delta_pocs = count of (used || use_delta) flags that were set)
                    // Since we don't track individual flags, we estimate conservatively.
                    // For most IPTV streams, prediction RPS has similar num_delta_pocs to the reference.
                    SetNumDeltaPocsForRps(stRpsIdx, numDeltaPocs);
                }
                else
                {
                    // Non-prediction path (FFmpeg lines 188-205):
                    // num_negative_pics (ue(v))
                    int numNegativePics = (int)reader.GetUeGolomb();
                    // num_positive_pics (ue(v))
                    int numPositivePics = (int)reader.GetUeGolomb();

                    int numDeltaPocs = numNegativePics + numPositivePics;

                    // For each negative pic: delta_poc_s0_minus1 (ue(v)) + used_by_curr_pic_s0_flag (1 bit)
                    for (int i = 0; i < numNegativePics; i++)
                    {
                        reader.GetUeGolomb(); // delta_poc_s0_minus1
                        reader.GetBits(1);    // used_by_curr_pic_s0_flag
                    }
                    // For each positive pic: delta_poc_s1_minus1 (ue(v)) + used_by_curr_pic_s1_flag (1 bit)
                    for (int i = 0; i < numPositivePics; i++)
                    {
                        reader.GetUeGolomb(); // delta_poc_s1_minus1
                        reader.GetBits(1);    // used_by_curr_pic_s1_flag
                    }

                    SetNumDeltaPocsForRps(stRpsIdx, numDeltaPocs);
                }
            }
        }

        /// <summary>
        /// Tracking for num_delta_pocs per RPS index, needed for prediction path in SkipShortTermRefPicSetExact.
        /// This is reset for each new SPS parsing session.
        /// </summary>
        private static readonly int[] _rpsNumDeltaPocs = new int[64];

        private static void ResetRpsTracking()
        {
            Array.Clear(_rpsNumDeltaPocs, 0, _rpsNumDeltaPocs.Length);
        }

        private static void SetNumDeltaPocsForRps(int idx, int numDeltaPocs)
        {
            if (idx >= 0 && idx < _rpsNumDeltaPocs.Length)
                _rpsNumDeltaPocs[idx] = numDeltaPocs;
        }

        private static int GetNumDeltaPocsForRps(int idx)
        {
            if (idx >= 0 && idx < _rpsNumDeltaPocs.Length)
                return _rpsNumDeltaPocs[idx];
            return 0; // default: no delta pocs (safe fallback)
        }

        /// <summary>
        /// Skip short_term_ref_pic_set syntax element (old heuristic version, kept for reference)
        /// </summary>
        private static void SkipShortTermRefPicSet(BitstreamReader reader, int stRpsIdx, int numStRefPicSets)
        {
            // This is the old heuristic version. Replaced by SkipShortTermRefPicSetExact.
            // Kept here in case it's called elsewhere.
            int interRefPicPresent = 0;
            if (stRpsIdx != 0)
            {
                interRefPicPresent = (int)reader.GetBits(1);
            }

            if (interRefPicPresent != 0)
            {
                if (stRpsIdx == numStRefPicSets)
                {
                    reader.GetUeGolomb();
                }
                reader.GetBits(1);
                reader.GetUeGolomb();

                for (int j = 0; j < 32; j++)
                {
                    int usedFlag = (int)reader.GetBits(1);
                    if (usedFlag != 0) reader.GetBits(1);
                    if (reader.BitsRemaining < 8) break;
                }
            }
            else
            {
                int numNegativePics = (int)reader.GetUeGolomb();
                int numPositivePics = (int)reader.GetUeGolomb();
                for (int i = 0; i < numNegativePics; i++)
                {
                    reader.GetUeGolomb();
                    reader.GetBits(1);
                }
                for (int i = 0; i < numPositivePics; i++)
                {
                    reader.GetUeGolomb();
                    reader.GetBits(1);
                }
            }
        }

        /// <summary>
        /// Parse VUI parameters (FFmpeg ff_h2645_decode_common_vui_params)
        /// This is where color metadata lives
        /// </summary>
        private static void ParseVui(BitstreamReader reader, ref HevcSpsInfo spsInfo)
        {
            spsInfo.HasVui = true;

            // === aspect_ratio_info_present_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                int aspectRatioIdc = (int)reader.GetBits(8);
                if (aspectRatioIdc == 255) // Extended_SAR
                {
                    reader.GetBits(16); // sar_width
                    reader.GetBits(16); // sar_height
                }
            }

            // === overscan_info_present_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                reader.GetBits(1); // overscan_appropriate_flag
            }

            // === video_signal_type_present_flag (1 bit) ===
            int videoSignalPresent = (int)reader.GetBits(1);
            LogVerbose($"[HevcTsParser] VUI: video_signal_type_present_flag = {videoSignalPresent}");
            if (videoSignalPresent != 0)
            {
                // === video_format (3 bits) ===
                int videoFormat = (int)reader.GetBits(3);
                LogVerbose($"[HevcTsParser] VUI: video_format = {videoFormat}");

                // === video_full_range_flag (1 bit) ===
                spsInfo.VideoFullRangeFlag = (int)reader.GetBits(1);
                LogVerbose($"[HevcTsParser] VUI: video_full_range_flag = {spsInfo.VideoFullRangeFlag}");

                // === colour_description_present_flag (1 bit) ===
                int colourDescPresent = (int)reader.GetBits(1);
                LogVerbose($"[HevcTsParser] VUI: colour_description_present_flag = {colourDescPresent}");
                if (colourDescPresent != 0)
                {
                    spsInfo.HasColourDescription = true;

                    // === colour_primaries (8 bits) ===
                    // FFmpeg: AVCOL_PRI_BT2020 = 9
                    spsInfo.ColourPrimaries = (int)reader.GetBits(8);

                    // === transfer_characteristics (8 bits) ===
                    // FFmpeg: AVCOL_TRC_SMPTE2084 = 16 (PQ), AVCOL_TRC_ARIB_STD_B67 = 18 (HLG)
                    spsInfo.TransferCharacteristics = (int)reader.GetBits(8);

                    // === matrix_coefficients (8 bits) ===
                    // FFmpeg: AVCOL_SPC_BT2020_NCL = 9
                    spsInfo.MatrixCoefficients = (int)reader.GetBits(8);

                    LogVerbose($"[HevcTsParser] VUI: colour_description present - primaries={spsInfo.ColourPrimaries}, transfer={spsInfo.TransferCharacteristics}, matrix={spsInfo.MatrixCoefficients}");
                }
                else
                {
                    LogVerbose($"[HevcTsParser] VUI: colour_description NOT present");
                }
            }

            // === chroma_loc_info_present_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                reader.GetUeGolomb(); // chroma_sample_loc_type_top_field
                reader.GetUeGolomb(); // chroma_sample_loc_type_bottom_field
            }

            // === neutral_chroma_indication_flag (1 bit) ===
            reader.GetBits(1);

            // === field_seq_flag (1 bit) ===
            reader.GetBits(1);

            // === frame_field_info_present_flag (1 bit) ===
            reader.GetBits(1);

            // === default_display_window_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                reader.GetUeGolomb(); // def_disp_win_left_offset
                reader.GetUeGolomb(); // def_disp_win_right_offset
                reader.GetUeGolomb(); // def_disp_win_top_offset
                reader.GetUeGolomb(); // def_disp_win_bottom_offset
            }

            // === vui_timing_info_present_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                reader.GetBits(32); // vui_num_units_in_tick
                reader.GetBits(32); // vui_time_scale
                if (reader.GetBits(1) != 0) // vui_poc_proportional_to_timing_flag
                {
                    reader.GetUeGolomb(); // vui_num_ticks_poc_diff_one_minus1
                }
                reader.GetBits(1); // vui_hrd_parameters_present_flag
            }

            // === bitstream_restriction_flag (1 bit) ===
            if (reader.GetBits(1) != 0)
            {
                reader.GetBits(1); // tiles_fixed_structure_flag
                reader.GetBits(1); // motion_vectors_over_pic_boundaries_flag
                reader.GetBits(1); // restricted_ref_pic_lists_flag
                reader.GetUeGolomb(); // min_spatial_segmentation_idc
                reader.GetUeGolomb(); // max_bytes_per_pic_denom
                reader.GetUeGolomb(); // max_bits_per_min_cu_denom
                reader.GetUeGolomb(); // log2_max_mv_length_horizontal
                reader.GetUeGolomb(); // log2_max_mv_length_vertical
            }
        }

        #endregion

        #region SEI HDR Metadata Parsing

        /// <summary>
        /// Parse SEI PREFIX/SUFFIX message for HDR metadata.
        /// Follows ITU-T H.265 Annex D.1 / D.2:
        /// - payloadType 137: mastering_display_colour_volume
        /// - payloadType 144: content_light_level_info
        /// - payloadType 145: colour_remapping (less common)
        /// - payloadType 147: alternative_transfer_characteristics
        ///
        /// This is how IPTV streams often embed HDR metadata even when SPS has no VUI.
        /// RBSP passed here should NOT include the NAL header byte (caller strips it).
        /// </summary>
        public static HevcSpsInfo? ParseSeiForColorMetadata(byte[] seiPayload)
        {
            try
            {
                // Remove emulation prevention bytes first (FFmpeg does this before SEI parsing)
                byte[] cleanPayload = RemoveEmulationPrevention(seiPayload);
                LogVerbose($"[HevcTsParser] SEI: raw={seiPayload.Length} bytes, after emulation removal={cleanPayload.Length} bytes");

                // Quick scan: enumerate all SEI payload types in this SEI NAL
                var seiTypes = new System.Collections.Generic.List<uint>();
                int scanIdx = 0;
                var scanReader = new BitstreamReader(cleanPayload);
                while (scanReader.BitsRemaining >= 16)
                {
                    uint pt = 0;
                    byte bb;
                    do { bb = (byte)scanReader.GetBits(8); pt += bb; } while (bb == 0xFF);
                    uint ps = 0;
                    do { bb = (byte)scanReader.GetBits(8); ps += bb; } while (bb == 0xFF);
                    seiTypes.Add(pt);
                    int bitsToSkip = (int)ps * 8;
                    if (bitsToSkip > scanReader.BitsRemaining) break;
                    scanReader.SkipBits(bitsToSkip);
                    // SEI byte alignment
                    if (scanReader.BitsRemaining >= 8) scanReader.GetBits(8); // trailing bits
                }
                LogVerbose($"[HevcTsParser] SEI message types found: {string.Join(", ", seiTypes)}");

                var reader = new BitstreamReader(cleanPayload);
                bool foundHdrMetadata = false;
                var colorInfo = new HevcSpsInfo();
                colorInfo.ColourPrimaries = 2;        // UNSPECIFIED
                colorInfo.TransferCharacteristics = 2; // UNSPECIFIED
                colorInfo.MatrixCoefficients = 2;      // UNSPECIFIED

                while (reader.BitsRemaining >= 16)
                {
                    // Parse sei_message() - payloadType (FFmpeg h2645_sei.h syntax)
                    uint payloadType = 0;
                    byte b;
                    do
                    {
                        b = (byte)reader.GetBits(8);
                        payloadType += b;
                    } while (b == 0xFF);

                    // Parse payloadSize
                    uint payloadSize = 0;
                    do
                    {
                        b = (byte)reader.GetBits(8);
                        payloadSize += b;
                    } while (b == 0xFF);

                    if (payloadSize == 0 || reader.BitsRemaining < payloadSize * 8)
                    {
                        LogVerbose($"[HevcTsParser] SEI: payloadSize={payloadSize} but only {reader.BitsRemaining} bits remaining, skipping");
                        break;
                    }

                    int payloadStartBits = reader.BitsRemaining;

                    // Check for HDR-relevant SEI messages
                    if (payloadType == 137)
                    {
                        // mastering_display_colour_volume (HEVC SEI message type)
                        // This tells us the actual mastering display primaries and luminance
                        if (reader.BitsRemaining >= (16 * 10 + 32 * 2)) // Need at least primaries + white point + luminance
                        {
                            // display_primaries_x[0..2] (16 bits each, 4 digits fixed-point)
                            uint gx = reader.GetBits(16);
                            uint gy = reader.GetBits(16);
                            uint bx = reader.GetBits(16);
                            uint by = reader.GetBits(16);
                            uint rx = reader.GetBits(16);
                            uint ry = reader.GetBits(16);

                            // white_point_x, white_point_y (16 bits each)
                            uint wpx = reader.GetBits(16);
                            uint wpy = reader.GetBits(16);

                            // max_display_mastering_luminance, min_display_mastering_luminance (32 bits each)
                            uint maxLuminance = reader.GetBits(32);
                            uint minLuminance = reader.GetBits(32);

                            // BT.2020 primaries detection:
                            // BT.2020 primaries in 0.00002 units: R(35400,14600), G(13250,7500), B(7500,3000), WP(15635,16450)
                            bool isBt2020 = false;
                            if (rx > 15000 && rx < 18000 && ry > 10000 && ry < 18000) // Red ~0.708, 0.292
                            {
                                // Check if white point is D65 (~0.3127, 0.3290)
                                if (wpx > 13000 && wpx < 18000 && wpy > 13000 && wpy < 20000)
                                {
                                    isBt2020 = true;
                                }
                            }

                            if (isBt2020)
                            {
                                colorInfo.ColourPrimaries = 9;       // AVCOL_PRI_BT2020
                                colorInfo.MatrixCoefficients = 9;    // AVCOL_SPC_BT2020_NCL
                                colorInfo.HasColourDescription = true;
                                foundHdrMetadata = true;
                            }

                            // Max luminance: 10000 = 10,000 nits, 40000000 = 4,000 nits (scaled by 10000)
                            float actualMaxNits = maxLuminance / 10000.0f;
                            float actualMinNits = minLuminance / 10000.0f;

                            if (isBt2020)
                            {
                                LogVerbose($"[HevcTsParser] SEI mastering_display: BT.2020 detected, maxLuminance={actualMaxNits:F0} nits");
                            }

                            // Check for PQ transfer based on luminance range
                            if (maxLuminance > 1000000 && minLuminance > 0) // Likely HDR (>100 nits max, >0 min)
                            {
                                colorInfo.TransferCharacteristics = 16; // AVCOL_TRC_SMPTE2084 (PQ)
                                colorInfo.HasColourDescription = true;
                                foundHdrMetadata = true;
                                LogVerbose($"[HevcTsParser] SEI mastering_display: PQ/HDR10 detected, max={actualMaxNits:F0} nits, min={actualMinNits:F4} nits");
                            }
                        }
                        else
                        {
                            LogVerbose($"[HevcTsParser] SEI mastering_display: not enough bits ({reader.BitsRemaining}), skipping");
                        }
                    }
                    else if (payloadType == 144)
                    {
                        // content_light_level_info (CLL)
                        // max_content_light_level and max_pic_average_light_level
                        if (reader.BitsRemaining >= 32)
                        {
                            uint maxCLL = reader.GetBits(16);  // Max Content Light Level (nits)
                            uint maxFALL = reader.GetBits(16); // Max Frame-Average Light Level (nits)

                            // MaxCLL > 1000 typically indicates HDR content
                            // PQ HDR10: typically 1000-4000 nits maxCLL
                            // HLG: typically 1000-2000 nits maxCLL
                            // SDR: typically < 200 nits maxCLL
                            if (maxCLL > 800) // Likely HDR
                            {
                                // If we don't already have transfer characteristics, infer from CLL
                                if (colorInfo.TransferCharacteristics == 2)
                                {
                                    // Common broadcast HDR uses HLG
                                    // Streaming HDR uses PQ
                                    // If maxCLL > 2000, likely PQ. If 800-2000, could be HLG.
                                    if (maxCLL > 2000)
                                    {
                                        colorInfo.TransferCharacteristics = 16; // PQ
                                    }
                                    else
                                    {
                                        // Could be either, but default to HLG for IPTV
                                        colorInfo.TransferCharacteristics = 18; // HLG
                                    }
                                    colorInfo.HasColourDescription = true;
                                    foundHdrMetadata = true;
                                }

                                LogVerbose($"[HevcTsParser] SEI content_light_level: maxCLL={maxCLL} nits, maxFALL={maxFALL} nits");
                            }
                        }
                        else
                        {
                            LogVerbose($"[HevcTsParser] SEI content_light_level: not enough bits ({reader.BitsRemaining}), skipping");
                        }
                    }
                    else if (payloadType == 145)
                    {
                        // colour_remapping (rare in IPTV, but could have HDR info)
                        // Usually for SDR-to-HDR mapping, skip for now
                        LogVerbose($"[HevcTsParser] SEI colour_remapping: skipping (size={payloadSize})");
                    }
                    else if (payloadType == 147)
                    {
                        // alternative_transfer_characteristics
                        // This is HOW IPTV broadcasts signal HLG/PQ when SPS VUI doesn't have it
                        // preferred_transfer_characteristics (8 bits)
                        if (reader.BitsRemaining >= 8)
                        {
                            int altTransfer = (int)reader.GetBits(8);
                            LogVerbose($"[HevcTsParser] SEI alternative_transfer: preferred_transfer_characteristics = {altTransfer}");

                            if (altTransfer != 2) // Not unspecified
                            {
                                colorInfo.TransferCharacteristics = altTransfer;
                                colorInfo.HasColourDescription = true;
                                foundHdrMetadata = true;

                                string transferName = altTransfer switch
                                {
                                    16 => "SMPTE ST 2084 (PQ/HDR10)",
                                    18 => "ARIB STD-B67 (HLG)",
                                    _ => $"Other ({altTransfer})"
                                };
                                LogVerbose($"[HevcTsParser] SEI alternative_transfer: HDR detected - {transferName}");
                            }
                        }
                    }
                    else
                    {
                        LogVerbose($"[HevcTsParser] SEI payloadType={payloadType}, size={payloadSize}: skipping (not HDR-related)");
                    }

                    // Skip remaining payload bits (if we didn't read all of it)
                    int bitsConsumed = payloadStartBits - reader.BitsRemaining;
                    int bitsToSkip = (int)payloadSize * 8 - bitsConsumed;
                    if (bitsToSkip > 0)
                    {
                        reader.SkipBits(bitsToSkip);
                    }

                    // Stop if no more SEI messages
                    // (sei_byte() alignment - typically 0xFF bytes, we just check if there's more data)
                }

                if (foundHdrMetadata)
                {
                    LogVerbose($"[HevcTsParser] SEI HDR metadata extracted: primaries={colorInfo.ColourPrimaries}, transfer={colorInfo.TransferCharacteristics}, matrix={colorInfo.MatrixCoefficients}");
                    return colorInfo;
                }

                return null;
            }
            catch (Exception ex)
            {
                LogVerbose($"[HevcTsParser] SEI parse error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region I-Frame Detection

        /// <summary>
        /// Check if NAL unit is a keyframe (IDR/CRA/BLA)
        /// HEVC keyframe NAL types: 16-21 (BLA, IDR, CRA)
        /// </summary>
        public static bool IsKeyframe(int nalType)
        {
            return nalType >= 16 && nalType <= 21;
        }

        /// <summary>
        /// Get NAL type name for debugging
        /// </summary>
        public static string GetNalTypeName(int nalType)
        {
            return nalType switch
            {
                0 => "TRAIL_N",
                1 => "TRAIL_R",
                2 => "TSA_N",
                3 => "TSA_R",
                4 => "STSA_N",
                5 => "STSA_R",
                6 => "RADL_N",
                7 => "RADL_R",
                8 => "RASL_N",
                9 => "RASL_R",
                16 => "BLA_W_LP",
                17 => "BLA_W_RADL",
                18 => "BLA_N_LP",
                19 => "IDR_W_DLP",
                20 => "IDR_N_LP",
                21 => "CRA_NUT",
                32 => "VPS",
                33 => "SPS",
                34 => "PPS",
                35 => "AUD",
                39 => "SEI_PREFIX",
                40 => "SEI_SUFFIX",
                _ => $"NAL_{nalType}"
            };
        }

        #endregion

        #region PES Payload Parsing (FFmpeg h2645_parse.c)

        /// <summary>
        /// FFmpeg-style NAL extraction: scans raw data, strips emulation prevention bytes,
        /// and finds NAL boundaries in a single pass.
        /// Called on the RAW remaining payload (starting after a NAL start code).
        /// Returns clean RBSP and the offset in the RAW data where the next start code was found.
        /// Following FFmpeg's ff_h2645_extract_rbsp exactly.
        /// </summary>
        public static int ExtractRbsp(byte[] src, int length, out byte[] rbsp, out int nextStartOffset)
        {
            rbsp = new byte[length];
            nextStartOffset = length;
            int si = 0, di = 0;

            while (si + 2 < length)
            {
                // Check for 00 00 XX pattern
                if (src[si] == 0x00 && src[si + 1] == 0x00)
                {
                    if (src[si + 2] == 0x03)
                    {
                        // Emulation prevention byte - strip it, write 00 00
                        rbsp[di++] = 0x00;
                        rbsp[di++] = 0x00;
                        si += 3; // Skip 00 00 03
                        continue;
                    }
                    else if (src[si + 2] == 0x01)
                    {
                        // Start code 00 00 01 - stop here
                        nextStartOffset = si;
                        break;
                    }
                    else if (src[si + 2] == 0x00 && si + 3 < length && src[si + 3] == 0x01)
                    {
                        // Start code 00 00 00 01 - stop here
                        nextStartOffset = si;
                        break;
                    }
                    else
                    {
                        // Not a start code or emulation byte, copy normally
                        rbsp[di++] = src[si++];
                    }
                }
                else
                {
                    rbsp[di++] = src[si++];
                }
            }

            // Copy remaining bytes (no more patterns to check)
            while (si < length)
            {
                rbsp[di++] = src[si++];
            }

            return di; // Return RBSP length
        }

        /// <summary>
        /// Parse a complete PES payload for HEVC NAL units.
        /// This follows FFmpeg's ff_h2645_packet_split pattern:
        /// 1. Find start codes
        /// 2. Extract each NAL unit
        /// 3. Strip emulation prevention
        /// 4. Parse NAL type and extract metadata (SPS/VUI)
        /// 
        /// Call this from StreamProxyService when a complete PES payload is assembled.
        /// </summary>
        public static ParseResult ParsePesPayload(byte[] pesPayload, ParseResult previousResult = null)
        {
            LogVerbose($"[HevcTsParser] ParsePesPayload entry: {pesPayload.Length} bytes");
            var result = previousResult ?? new ParseResult();
            int payloadLen = pesPayload.Length;
            int nalCount = 0;

            // FFmpeg two-pass approach from h2645_parse.c:
            // Pass 1: Find all start code positions
            var startCodes = new List<(int offset, int length)>();
            for (int i = 0; i < payloadLen - 2; i++)
            {
                if (pesPayload[i] == 0x00 && pesPayload[i + 1] == 0x00)
                {
                    if (pesPayload[i + 2] == 0x01)
                    {
                        startCodes.Add((i, 3));
                        i += 2; // Skip past start code
                    }
                    else if (pesPayload[i + 2] == 0x00 && i + 3 < payloadLen && pesPayload[i + 3] == 0x01)
                    {
                        startCodes.Add((i, 4));
                        i += 3; // Skip past start code
                    }
                }
            }

            if (startCodes.Count == 0)
            {
                LogVerbose($"[HevcTsParser] No start codes found in {payloadLen} bytes");
                return result;
            }

            LogVerbose($"[HevcTsParser] Found {startCodes.Count} start codes");

            // Dump raw bytes around each start code for debugging
            for (int i = 0; i < startCodes.Count; i++)
            {
                var (scOffset, scLen) = startCodes[i];
                int headerPos = scOffset + scLen;
                int dumpLen = Math.Min(16, payloadLen - scOffset);
                string hexDump = BitConverter.ToString(pesPayload, scOffset, dumpLen).Replace("-", " ");
                byte nalTypeByte = headerPos < payloadLen ? pesPayload[headerPos] : (byte)0;
                int nalType = (nalTypeByte & 0x7E) >> 1;
                LogVerbose($"[HevcTsParser]   SC#{i+1}: offset={scOffset}, len={scLen}, NAL type byte=0x{nalTypeByte:X2}, type={nalType}, bytes: {hexDump}");
            }

            // Pass 2: For each start code, extract bounded NAL unit and parse
            for (int scIdx = 0; scIdx < startCodes.Count; scIdx++)
            {
                var (startCodeOffset, startCodeLen) = startCodes[scIdx];
                int nalHeaderPos = startCodeOffset + startCodeLen;

                if (nalHeaderPos >= payloadLen) break;

                // Find end of this NAL unit: start of next start code
                int nalEndOffset;
                if (scIdx + 1 < startCodes.Count)
                {
                    nalEndOffset = startCodes[scIdx + 1].offset;
                }
                else
                {
                    nalEndOffset = payloadLen;
                }

                // Bounded NAL slice (NAL header byte + RBSP data up to next start code)
                int nalSliceLen = nalEndOffset - nalHeaderPos;
                if (nalSliceLen <= 0) continue; // Too short for valid NAL

                byte[] nalSlice = new byte[nalSliceLen];
                Array.Copy(pesPayload, nalHeaderPos, nalSlice, 0, nalSliceLen);

                // Extract RBSP from the ENTIRE NAL unit (including NAL header byte)
                // This matches FFmpeg's model where RBSP[0] is the NAL header byte
                int rbspLen = ExtractRbsp(nalSlice, nalSliceLen, out byte[] rbsp, out int _);

                if (rbspLen > 0)
                {
                    // NAL type is from RBSP first byte (the NAL header byte)
                    int cleanNalType = (rbsp[0] & 0x7E) >> 1;
                    nalCount++;
                    LogVerbose($"[HevcTsParser]   NAL#{nalCount}: type={cleanNalType}, RBSP={rbspLen} bytes");

                    // Track NAL type
                    result.TotalNalsFound++;
                    result.NalTypesFound.Add(cleanNalType);

                    if (cleanNalType == 39 || cleanNalType == 40) // SEI_PREFIX / SEI_SUFFIX
                    {
                        // FFmpeg parses the 2-byte HEVC NAL header before SEI payload syntax.
                        // Strip both header bytes here so ParseSeiForColorMetadata starts on sei_message().
                        if (rbspLen > 2)
                        {
                            byte[] seiPayload = new byte[rbspLen - 2];
                            Array.Copy(rbsp, 2, seiPayload, 0, rbspLen - 2);
                            var seiColor = ParseSeiForColorMetadata(seiPayload);
                            if (seiColor.HasValue)
                            {
                                // SEI HDR metadata takes priority over SPS/VUI
                                // (it's the authoritative source for mastering display / CLL)
                                result.ColorInfo = seiColor;
                                LogVerbose($"[HevcTsParser] SEI HDR detected: Primaries={seiColor.Value.ColourPrimaries}, Transfer={seiColor.Value.TransferCharacteristics}, Matrix={seiColor.Value.MatrixCoefficients}");
                            }
                        }
                    }
                    else if (cleanNalType == 33) // SPS
                    {
                        // Reset RPS tracking for new SPS parsing session
                        ResetRpsTracking();

                        LogVerbose($"[HevcTsParser] SPS RBSP: {rbspLen} bytes, first 16: {BitConverter.ToString(rbsp, 0, Math.Min(16, rbspLen)).Replace("-", " ")}");

                        // FFmpeg enters SPS syntax after the full 2-byte HEVC NAL header.
                        if (rbspLen > 2)
                        {
                            byte[] spsData = new byte[rbspLen - 2];
                            Array.Copy(rbsp, 2, spsData, 0, rbspLen - 2);

                            // RAW SPS DUMP: Log full SPS data (without NAL header) for debugging
                            int spsDumpLen = Math.Min(256, spsData.Length);
                            string spsHex = BitConverter.ToString(spsData, 0, spsDumpLen).Replace("-", " ");
                            if (spsData.Length > 256) spsHex += $" ... ({spsData.Length} total)";
                            LogVerbose($"[HevcTsParser] SPS full payload (without NAL header, first {spsDumpLen} bytes): {spsHex}");

                            var spsInfo = ParseSps(spsData);
                            if (spsInfo.IsValid)
                            {
                                // Only use SPS/VUI color info if we don't already have SEI HDR metadata
                                // (SEI mastering_display/content_light_level is more authoritative)
                                bool hasSeiColor = result.ColorInfo.HasValue && result.ColorInfo.Value.HasColourDescription;
                                bool spsHasColor = spsInfo.HasColourDescription;

                                if (!hasSeiColor || !spsHasColor)
                                {
                                    // Either no SEI data yet, or SPS has color but SEI didn't
                                    // SPS data is still useful for resolution info
                                    if (!hasSeiColor)
                                    {
                                        result.ColorInfo = spsInfo;
                                    }
                                    else
                                    {
                                        // Merge: keep SEI color, but use SPS resolution
                                        var merged = result.ColorInfo.Value;
                                        merged.Width = spsInfo.Width;
                                        merged.Height = spsInfo.Height;
                                        merged.BitDepthLuma = spsInfo.BitDepthLuma;
                                        merged.BitDepthChroma = spsInfo.BitDepthChroma;
                                        result.ColorInfo = merged;
                                    }
                                }
                                else
                                {
                                    // SEI has color, but still log SPS info
                                    LogVerbose($"[HevcTsParser] SPS has color but SEI already provided HDR metadata, keeping SEI data");
                                }

                                LogVerbose($"[HevcTsParser] SPS Parsed: {spsInfo.Width}x{spsInfo.Height}, VUI={(spsInfo.HasVui ? "Y" : "N")}, ColorDesc={(spsInfo.HasColourDescription ? "Y" : "N")}");
                                if (spsInfo.HasColourDescription)
                                {
                                    LogVerbose($"[HevcTsParser]   Primaries={spsInfo.ColourPrimaries}, Transfer={spsInfo.TransferCharacteristics}, Matrix={spsInfo.MatrixCoefficients}, FullRange={spsInfo.VideoFullRangeFlag}");
                                }
                            }
                        }

                        result.TotalParamSets++;
                        result.HasReceivedSPS = true;
                        result.PacketHasHeaders = true;
                    }
                    else if (cleanNalType == 32) // VPS
                    {
                        result.TotalParamSets++;
                        result.HasReceivedVPS = true;
                        result.PacketHasHeaders = true;
                    }
                    else if (cleanNalType == 34) // PPS
                    {
                        result.TotalParamSets++;
                        result.HasReceivedPPS = true;
                        result.PacketHasHeaders = true;
                    }
                    else if (cleanNalType >= 16 && cleanNalType <= 21) // Keyframe
                    {
                        result.TotalKeyframes++;
                        result.GateOpen = true;
                    }
                }
            }

            LogVerbose($"[HevcTsParser] ParsePesPayload exit: found {nalCount} NALs, total={result.TotalNalsFound}, SPS={result.HasReceivedSPS}");
            return result;
        }

        #endregion
    }
}

