using System;
using System.Runtime.InteropServices;

namespace ModernIPTVPlayer.Models.Metadata
{
    /// <summary>
    /// Binary Search Index için 64-bit hizalamalı token kaydı.
    /// MMF üzerinden doğrudan pointer casting ile okunmak üzere tasarlanmıştır.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
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
    /// Substring (alt dizgi) aramaları için Trigram kaydı.
    /// 3 karakterli UTF-8 hash bilgisini indeks listesine bağlar.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
    public readonly struct TrigramRecord
    {
        public readonly uint TrigramHash; // 24-bit hash (3 bytes)
        public readonly int IndicesOff;
        public readonly ushort IndicesLen;

        public TrigramRecord(uint hash, int off, ushort len)
        {
            TrigramHash = hash;
            IndicesOff = off;
            IndicesLen = len;
        }
    }

    /// <summary>
    /// Index dosyasının başlık (header) yapısı.
    /// Pinnacle V4: Trigram desteği ve Cache-Line hizalaması eklendi.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
    public readonly struct IndexHeader
    {
        public const long V4Magic = 0x34564E4942584449; // "IDXBINV4"

        public readonly long Magic;        
        public readonly int Version;       // 4
        public readonly long CreatedAt;
        
        public readonly int TokenCount;
        public readonly int TokenTableOff;
        
        public readonly int TrigramCount;
        public readonly int TrigramTableOff;
        
        public readonly int IndicesHeapOff;
        public readonly int StringHeapOff;
        
        // Alignment padding (64-byte total)
        private readonly long _pad1;
        private readonly int _pad2;

        public IndexHeader(int tokenCount, int tokenTableOff, int trigramCount, int trigramTableOff, int indicesHeapOff, int stringHeapOff)
        {
            Magic = V4Magic;
            Version = 4;
            CreatedAt = DateTime.UtcNow.Ticks;
            TokenCount = tokenCount;
            TokenTableOff = tokenTableOff;
            TrigramCount = trigramCount;
            TrigramTableOff = trigramTableOff;
            IndicesHeapOff = indicesHeapOff;
            StringHeapOff = stringHeapOff;
            _pad1 = 0;
            _pad2 = 0;
        }
    }
}
