using System;
using System.Runtime.InteropServices;

namespace ModernIPTVPlayer.Models.Metadata
{
    /// <summary>
    /// PROJECT ZERO: Phase 2 - High-performance VOD record structure.
    /// Designed for 8-byte alignment and direct Memory-Mapped casting.
    /// Total Size: 128 bytes (2x Cache-line).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VodRecord
    {
        // IDs & Basic Tracking (24 bytes)
        public int StreamId;            // 4
        public int CategoryId;          // 4
        public int PriorityScore;       // 4
        public uint Fingerprint;        // 4
        public long LastModified;       // 8
        
        // String Pointers (Offsets into MetadataBuffer) (13 x 8 = 104 bytes)
        public int NameOff, NameLen;    // 8
        public int IconOff, IconLen;    // 8
        public int ImdbIdOff, ImdbIdLen;// 8
        public int PlotOff, PlotLen;    // 8
        public int YearOff, YearLen;    // 8
        public int GenresOff, GenresLen;// 8
        public int CastOff, CastLen;    // 8
        public int DirectorOff, DirectorLen; // 8
        public int TrailerOff, TrailerLen;// 8
        public int BackdropOff, BackdropLen;// 8
        public int SourceTitleOff, SourceTitleLen;// 8
        public int RatingOff, RatingLen;// 8
        public int ExtOff, ExtLen;      // 8 [NEW]
        
        // Metadata Flags & Scaled Values (8 bytes)
        public short RatingScaled;      // 2
        public byte Flags;              // 1
        public byte Reserved1;          // 1
        public int Duration;            // 4 [NEW]
        
        // Total Size: 24 + 104 + 8 = 136 bytes (8-byte aligned).
    }

    /// <summary>
    /// PROJECT ZERO: Phase 2 - High-performance Series record structure.
    /// Total Size: 136 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SeriesRecord
    {
        // IDs & Basic Tracking (24 bytes)
        public int SeriesId;            // 4
        public int CategoryId;          // 4
        public int PriorityScore;       // 4
        public uint Fingerprint;        // 4
        public long LastModified;       // 8
        
        // String Pointers (104 bytes)
        public int NameOff, NameLen;    // 8
        public int IconOff, IconLen;    // 8
        public int ImdbIdOff, ImdbIdLen;// 8
        public int PlotOff, PlotLen;    // 8
        public int YearOff, YearLen;    // 8
        public int GenresOff, GenresLen;// 8
        public int CastOff, CastLen;    // 8
        public int DirectorOff, DirectorLen; // 8
        public int TrailerOff, TrailerLen; // 8
        public int BackdropOff, BackdropLen; // 8
        public int SourceTitleOff, SourceTitleLen; // 8
        public int RatingOff, RatingLen; // 8
        public int ExtOff, ExtLen;      // 8 [NEW]

        // Tail (8 bytes)
        public short RatingScaled;      // 2
        public byte Flags;              // 1
        public byte Reserved1;          // 1
        public int AirTime;             // 4 [NEW: Reuse for Average Episode Runtime]
        
        // Total: 24+104+8 = 136 bytes.
    }
    
    /// <summary>
    /// PROJECT ZERO: Phase 4 - High-performance Category record structure.
    /// Compact 16-byte record for mapping categories to MetadataBuffer strings.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CategoryRecord
    {
        public int IdOff, IdLen;
        public int NameOff, NameLen;
    }
}
