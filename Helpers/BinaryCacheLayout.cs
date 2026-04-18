using System.IO;
using System.IO.MemoryMappedFiles;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Single source of truth for binary cache headers and fixed offsets.
    /// </summary>
    public static class BinaryCacheLayout
    {
        public const int HeaderSize = 32;
        public const int MagicOffset = 0;
        public const int VersionOffset = 4;
        public const int CountOffset = 8;
        public const int StringsLengthOffset = 12;
        public const int DirtyOffset = 16;
        public const int FingerprintOffset = 24;

        public const int VodMagic = 0x564F4444;
        public const int SeriesMagic = 0x53455244;
        public const int CategoryMagic = 0x43415443;
        public const int LiveMagic = 0x4256494C;

        public const int VodSeriesVersion = 4;
        public const int LiveVersion = 3;
        public const int VodIndexEntrySize = 8;

        public readonly record struct Header(int Magic, int Version, int Count, int StringsLength, bool IsDirty);

        public static void WriteHeader(BinaryWriter writer, int magic, int version, int count, int stringsLength, bool dirty)
        {
            writer.Write(magic);
            writer.Write(version);
            writer.Write(count);
            writer.Write(stringsLength);
            writer.Write((byte)(dirty ? 1 : 0));
            writer.Write(new byte[15]);
        }

        public static Header ReadHeader(MemoryMappedViewAccessor accessor)
            => new(
                accessor.ReadInt32(MagicOffset),
                accessor.ReadInt32(VersionOffset),
                accessor.ReadInt32(CountOffset),
                accessor.ReadInt32(StringsLengthOffset),
                accessor.ReadByte(DirtyOffset) == 1);

        public static Header ReadHeader(BinaryReader reader)
        {
            var header = new Header(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadByte() == 1);

            reader.BaseStream.Seek(HeaderSize, SeekOrigin.Begin);
            return header;
        }

        public static bool IsKnownMagic(int magic)
            => magic is VodMagic or SeriesMagic or CategoryMagic or LiveMagic;

        public static long GetRecordsOffset() => HeaderSize;

        public static long GetVodStringsOffset(int count, int recordSize)
            => HeaderSize + count * (long)recordSize + count * (long)VodIndexEntrySize;

        public static long GetSeriesStringsOffset(int count, int recordSize)
            => HeaderSize + count * (long)recordSize;

        public static long GetCategoryStringsOffset(int count, int recordSize)
            => HeaderSize + count * (long)recordSize;

        public static long GetStringsOffset(int magic, int count, int recordSize)
            => magic == VodMagic
                ? GetVodStringsOffset(count, recordSize)
                : HeaderSize + count * (long)recordSize;
    }
}
