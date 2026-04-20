using System;
using System.Runtime.InteropServices;

namespace ModernIPTVPlayer.Models.Metadata
{
    /// <summary>
    /// Binary Search Index için 64-bit hizalamalı token kaydı.
    /// MMF üzerinden doğrudan pointer casting ile okunmak üzere tasarlanmıştır.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct IndexTokenRecord
    {
        /// <summary>
        /// Yazı (String) yığınındaki UTF-8 başlangıç konumu.
        /// </summary>
        public readonly int NameOff;

        /// <summary>
        /// Yazı (String) uzunluğu (byte cinsinden).
        /// </summary>
        public readonly ushort NameLen;

        /// <summary>
        /// Bu terime ait kanal/içerik indekslerinin (int[]) başlangıç konumu.
        /// </summary>
        public readonly int IndicesOff;

        /// <summary>
        /// Bu terimle eşleşen toplam içerik sayısı.
        /// </summary>
        public readonly ushort IndicesLen;

        public IndexTokenRecord(int nameOff, ushort nameLen, int indicesOff, ushort indicesLen)
        {
            NameOff = nameOff;
            NameLen = nameLen;
            IndicesOff = indicesOff;
            IndicesLen = indicesLen;
        }
    }

    /// <summary>
    /// Index dosyasının başlık (header) yapısı.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
    public readonly struct IndexHeader
    {
        public readonly long Magic;        // "IDXBINV3"
        public readonly int Version;      // 3
        public readonly long CreatedAt;
        public readonly int TokenCount;
        public readonly int TokenTableOff;
        public readonly int IndicesHeapOff;
        public readonly int StringHeapOff;
        
        // Pad for 64-byte alignment
        private readonly long _pad1;
        private readonly long _pad2;
        private readonly int _pad3;

        public IndexHeader(int tokenCount, int tokenTableOff, int indicesHeapOff, int stringHeapOff)
        {
            Magic = 0x33564E4942584449; // "IDXBINV3"
            Version = 3;
            CreatedAt = DateTime.UtcNow.Ticks;
            TokenCount = tokenCount;
            TokenTableOff = tokenTableOff;
            IndicesHeapOff = indicesHeapOff;
            StringHeapOff = stringHeapOff;
            _pad1 = 0;
            _pad2 = 0;
            _pad3 = 0;
        }
    }
}
