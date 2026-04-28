using System;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Helpers;

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
        string? SourceTitle { get; set; }
        string SourceDisplayName => !string.IsNullOrEmpty(SourceTitle) ? SourceTitle : Title;

        // UI Binding Properties
        BitmapImage? PosterBitmap => string.IsNullOrEmpty(PosterUrl) ? null : SharedImageManager.GetOptimizedImage(PosterUrl, targetWidth: 150);
        bool HasNoPoster => string.IsNullOrWhiteSpace(PosterUrl) || PosterUrl.Equals("null", StringComparison.OrdinalIgnoreCase) || PosterUrl.Length <= 10;

        // Extended Metadata
        string? Genres { get; set; }
        string? Cast { get; set; }
        string? Director { get; set; }
        string? TrailerUrl { get; set; }
        int MetadataPriority { get; set; }
        int PriorityScore { get; set; }
        uint Fingerprint { get; set; }

        // UI Binding properties for PosterCard
        double ProgressValue { get; }
        bool ShowProgress { get; }
        string BadgeText { get; }
        bool ShowBadge { get; }
        
        // Source Identification
        string SourceBadgeText { get; }
        bool ShowSourceBadge { get; }

        // [STREMIO_BINDING_SYNC] Properties for polymorphic binding in DiscoveryControl
        string LandscapeImageUrl => BackdropUrl ?? PosterUrl;
        string DisplaySubtext => Genres ?? "";
        bool IsContinueWatching { get; set; }
        bool IsNotContinueWatching => !IsContinueWatching;
        string? EpisodeSubtext { get; set; }
        
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

        void Pin();
        void Unpin();
    }

    /// <summary>
    /// [PHASE 4.5] High-performance interface for virtualized stream collections.
    /// Allows the StreamMatchIndexer to perform zero-allocation MMF scans.
    /// </summary>
    public interface IVirtualStreamList
    {
        int Count { get; }
        long Fingerprint { get; }
        
        string? GetId(int index);
        int GetStreamId(int index);

        /// <summary>
        /// Retrieves the title into a provided buffer without creating a managed string object.
        /// </summary>
        ReadOnlySpan<char> GetTitleSpan(int index, Span<char> buffer);

        /// <summary>
        /// Retrieves the category ID into a provided buffer without creating a managed string object.
        /// </summary>
        ReadOnlySpan<char> GetCategorySpan(int index, Span<char> buffer);

        /// <summary>
        /// Performs a high-speed parallel scan of the entire dataset to build an index map
        /// without hydrating managed objects. Thread-safe using list locking.
        /// </summary>
        void ParallelScanInto(System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<int>> indexMap);
        
        void AddRef();
        BinaryCacheSession GetSession();
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

    public class ColorExtractedEventArgs : EventArgs
    {
        public Windows.UI.Color Primary { get; }
        public Windows.UI.Color Secondary { get; }

        public ColorExtractedEventArgs(Windows.UI.Color primary, Windows.UI.Color secondary)
        {
            Primary = primary;
            Secondary = secondary;
        }
    }

    public class SpotlightItemClickedEventArgs : EventArgs
    {
        public IMediaStream Stream { get; }
        public Microsoft.UI.Xaml.UIElement SourceElement { get; }
        public Microsoft.UI.Xaml.Media.ImageSource? PreloadedLogo { get; }

        public SpotlightItemClickedEventArgs(IMediaStream stream, Microsoft.UI.Xaml.UIElement sourceElement, Microsoft.UI.Xaml.Media.ImageSource? preloadedLogo)
        {
            Stream = stream;
            SourceElement = sourceElement;
            PreloadedLogo = preloadedLogo;
        }
    }

    public class TrailerExpandRequestedEventArgs : EventArgs
    {
        public IMediaStream Stream { get; }
        public Microsoft.UI.Xaml.UIElement SourceElement { get; }

        public TrailerExpandRequestedEventArgs(IMediaStream stream, Microsoft.UI.Xaml.UIElement sourceElement)
        {
            Stream = stream;
            SourceElement = sourceElement;
        }
    }

    public class CatalogItemClickedEventArgs : EventArgs
    {
        public IMediaStream Stream { get; }
        public Microsoft.UI.Xaml.UIElement SourceElement { get; }

        public CatalogItemClickedEventArgs(IMediaStream stream, Microsoft.UI.Xaml.UIElement sourceElement)
        {
            Stream = stream;
            SourceElement = sourceElement;
        }
    }

    public class DetailsRequestedEventArgs : EventArgs
    {
        public IMediaStream Stream { get; }
        public TmdbMovieResult? Tmdb { get; }

        public DetailsRequestedEventArgs(IMediaStream stream, TmdbMovieResult? tmdb)
        {
            Stream = stream;
            Tmdb = tmdb;
        }
    }
}
