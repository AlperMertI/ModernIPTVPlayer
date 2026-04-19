using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Binary Cache Layout: 64-byte aligned headers and zero-allocation IO.
    /// No legacy V1 support.
    /// </summary>
    public static class BinaryCacheLayout
    {
        public const int HeaderSize = 64; 
        public const int MagicOffset = 0;
        public const int VersionOffset = 4;
        public const int CountOffset = 8;
        public const int StringsLengthOffset = 12;
        public const int DirtyOffset = 16;
        public const int FingerprintOffset = 24;

        public const int Magic = 0x32565450; 
        public const int Version = 5; 

        public const int VodMagic = 0x32565450;   // "PTV2"
        public const int SeriesMagic = 0x32535450; // "PTS2"
        public const int CategoryMagic = 0x32435450; // "PTC2"
        public const int LiveMagic = 0x324C5450;   // "PTL2"
        
        public const int CurrentVersion = 5;
        public const int VodSeriesVersion = 5;
        public const int LiveVersion = 5;
        
        public const int VodIndexEntrySize = 8;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1, Size = 64)]
        public readonly record struct Header(int Magic, int Version, int Count, int StringsLength, bool IsDirty, long Fingerprint);

        public static void WriteHeader(BinaryWriter writer, int count, int stringsLength, bool dirty, long fingerprint)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(count);
            writer.Write(stringsLength);
            writer.Write((byte)(dirty ? 1 : 0));
            
            Span<byte> pad = stackalloc byte[7];
            pad.Clear();
            writer.Write(pad);
            
            writer.Write(fingerprint);

            Span<byte> finalPad = stackalloc byte[32];
            finalPad.Clear();
            writer.Write(finalPad);
        }

        public static Header ReadHeader(MemoryMappedViewAccessor accessor)
            => new(
                accessor.ReadInt32(MagicOffset),
                accessor.ReadInt32(VersionOffset),
                accessor.ReadInt32(CountOffset),
                accessor.ReadInt32(StringsLengthOffset),
                accessor.ReadByte(DirtyOffset) == 1,
                accessor.ReadInt64(FingerprintOffset));

        public static Header ReadHeader(BinaryReader reader)
        {
            var magic = reader.ReadInt32();
            var version = reader.ReadInt32();
            var count = reader.ReadInt32();
            var stringsLength = reader.ReadInt32();
            var dirty = reader.ReadByte() == 1;
            
            reader.BaseStream.Seek(FingerprintOffset, SeekOrigin.Begin);
            var fingerprint = reader.ReadInt64();

            var header = new Header(magic, version, count, stringsLength, dirty, fingerprint);
            reader.BaseStream.Seek(HeaderSize, SeekOrigin.Begin);
            return header;
        }

        public static bool IsKnownMagic(int magic) => magic == Magic;

        public static long GetRecordsOffset() => HeaderSize;

        public static long GetStringsOffset(int magic, int count, int recordSize)
             => HeaderSize + count * (long)recordSize;

        public static long GetStringsOffsetWithIndex(int count, int recordSize, int indexEntrySize)
            => HeaderSize + count * (long)recordSize + count * (long)indexEntrySize;
    }
}
