using System;

namespace ModernIPTVPlayer.Models
{
    public interface IMediaStream
    {
        int Id { get; }
        string? IMDbId { get; }
        string Title { get; }
        string PosterUrl { get; }
        string Rating { get; }
        string StreamUrl { get; set; }
        TmdbMovieResult TmdbInfo { get; set; }

        // UI Binding properties for PosterCard
        double ProgressValue { get; }
        bool ShowProgress { get; }
        string BadgeText { get; }
        bool ShowBadge { get; }
        
        // Source Identification
        string SourceBadgeText { get; }
        bool ShowSourceBadge { get; }
        
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
        public Microsoft.UI.Xaml.UIElement SourceElement { get; set; }

        public MediaNavigationArgs(IMediaStream stream, TmdbMovieResult tmdbInfo = null, bool autoResume = false, Microsoft.UI.Xaml.UIElement sourceElement = null)
        {
            Stream = stream;
            TmdbInfo = tmdbInfo ?? stream.TmdbInfo;
            AutoResume = autoResume;
            SourceElement = sourceElement;
        }
    }
}
