using System;

namespace ModernIPTVPlayer.Models
{
    public interface IMediaStream
    {
        int Id { get; }
        string Title { get; }
        string PosterUrl { get; }
        string Rating { get; }
        TmdbMovieResult TmdbInfo { get; set; }
        
        // Technical Metadata properties for polymorphism
        string Resolution { get; set; }
        string Fps { get; set; }
        string Codec { get; set; }
        long Bitrate { get; set; }
        bool IsHdr { get; set; }
        bool IsProbing { get; set; }
        bool? IsOnline { get; set; }
        bool HasMetadata { get; }
        // We can add more common properties as needed for the Details Page
    }

    public class MediaNavigationArgs
    {
        public IMediaStream Stream { get; set; }
        public TmdbMovieResult TmdbInfo { get; set; }

        public bool AutoResume { get; set; }

        public MediaNavigationArgs(IMediaStream stream, TmdbMovieResult tmdbInfo = null, bool autoResume = false)
        {
            Stream = stream;
            TmdbInfo = tmdbInfo ?? stream.TmdbInfo;
            AutoResume = autoResume;
        }
    }
}
