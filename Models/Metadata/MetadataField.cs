using System;

namespace ModernIPTVPlayer.Models.Metadata
{
    [Flags]
    public enum MetadataField : long
    {
        None = 0,
        Title = 1L << 0,
        Overview = 1L << 1,
        Year = 1L << 2,
        Rating = 1L << 3,
        Genres = 1L << 4,
        Poster = 1L << 5,
        Backdrop = 1L << 6,
        Trailer = 1L << 7,
        Runtime = 1L << 8,
        Logo = 1L << 9,
        Cast = 1L << 10,
        Seasons = 1L << 11,
        OriginalTitle = 1L << 12,
        Gallery = 1L << 13,
        CastPortraits = 1L << 14
    }
}
