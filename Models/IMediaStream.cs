using System;

namespace ModernIPTVPlayer.Models
{
    public interface IMediaStream
    {
        int Id { get; }
        string? IMDbId { get; }
        bool IsAvailableOnIptv { get; set; }
        string Title { get; set; }
        string? Description { get; set; }
        string PosterUrl { get; set; }
        string? BackdropUrl { get; set; }
        string Rating { get; set; }
        string? Type { get; }
        string Year { get; set; }
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
        
        /// <summary>
        /// Synchronizes the stream properties with the provided unified metadata.
        /// </summary>
        void UpdateFromUnified(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified);
    }

    public class MediaNavigationArgs
    {
        public IMediaStream Stream { get; set; }
        public TmdbMovieResult TmdbInfo { get; set; }
        public bool AutoResume { get; set; }
        public Microsoft.UI.Xaml.UIElement SourceElement { get; set; }
        public Microsoft.UI.Xaml.Media.ImageSource PreloadedImage { get; set; }
        public Microsoft.UI.Xaml.Media.ImageSource PreloadedLogo { get; set; }

        public MediaNavigationArgs(IMediaStream stream, TmdbMovieResult tmdbInfo = null, bool autoResume = false, Microsoft.UI.Xaml.UIElement sourceElement = null, Microsoft.UI.Xaml.Media.ImageSource preloadedImage = null, Microsoft.UI.Xaml.Media.ImageSource preloadedLogo = null)
        {
            Stream = stream;
            TmdbInfo = tmdbInfo ?? stream.TmdbInfo;
            AutoResume = autoResume;
            SourceElement = sourceElement;
            PreloadedImage = preloadedImage;
            PreloadedLogo = preloadedLogo;
        }
    }
    public enum MediaType { Movie, Series }

    public class MediaLibraryArgs
    {
        public MediaType Type { get; set; }
        public string? InitialCategoryId { get; set; }
    }
}
