using System;
using System.ComponentModel;

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models.Stremio
{
    public class StremioMediaStream : IMediaStream, INotifyPropertyChanged
    {
        public StremioMeta Meta { get; set; }
        
        public StremioMediaStream() { Meta = new StremioMeta(); }
        
        public StremioMediaStream(StremioMeta meta)
        {
            Meta = meta;
            TryPreEnrichFromCache();
        }

        private void TryPreEnrichFromCache()
        {
            if (string.IsNullOrEmpty(Meta.Background) && !string.IsNullOrEmpty(Meta.Id))
            {
                try
                {
                    // Try to find in TMDB Cache synchronously (RAM)
                    string typeKey = IsSeries ? "tv" : "movie";
                    string idKey = Meta.Id.StartsWith("tt") ? $"{typeKey}_id_{Meta.Id}" : null;
                    
                    if (idKey != null)
                    {
                        var cached = Services.TmdbCacheService.Instance.Get<TmdbMovieResult>(idKey);
                        if (cached != null && !string.IsNullOrEmpty(cached.BackdropPath))
                        {
                            Meta.Background = $"https://image.tmdb.org/t/p/w1280{cached.BackdropPath}";
                            System.Diagnostics.Debug.WriteLine($"[StremioStream] Pre-enriched Backdrop from Cache for {Meta.Name}");
                        }
                    }
                }
                catch { /* Silent fail for pre-enrichment */ }
            }
        }

        public string SourceAddon { get; set; }
        public int SourceIndex { get; set; } = 999; // Default to low priority position

        // IMediaStream Implementation
        public int Id => Meta.Id.GetHashCode(); // Temporary Int ID for interface
        public string IMDbId => Meta.Id; // Real ID

        public string Title { get => Meta.Name; set => Meta.Name = value; }

        // PosterUrl: uses override (set by async enrichment) if available, else Meta.Poster
        private string _overridePosterUrl;
        public string PosterUrl
        {
            get => _overridePosterUrl ?? Meta.Poster;
            set
            {
                if (_overridePosterUrl != value)
                {
                    _overridePosterUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _logoUrl;
        public string LogoUrl
        {
            get => _logoUrl;
            set
            {
                if (_logoUrl != value)
                {
                    _logoUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Rating => string.IsNullOrEmpty(Meta.ImdbRating) || Meta.ImdbRating == "N/A" || Meta.ImdbRating == "Unknown" ? "" : Meta.ImdbRating;
        public string StreamUrl { get; set; } = "";
        public string? BackdropUrl => Banner;
        
        // Use Backdrop/Banner for Landscape cards if available, else fallback to Poster.
        public string LandscapeImageUrl => !string.IsNullOrEmpty(Banner) ? Banner : PosterUrl;

        public bool IsContinueWatching { get; set; }
        public bool IsNotContinueWatching => !IsContinueWatching;
        public bool IsMovie => Meta.Type?.ToLower() == "movie";
        public bool IsSeries => Meta.Type?.ToLower() == "series" || Meta.Type?.ToLower() == "tv";

        public void UpdateBackground(string url)
        {
            if (Meta.Background != url)
            {
                Meta.Background = url;
                OnPropertyChanged(nameof(Banner));
                OnPropertyChanged(nameof(LandscapeImageUrl));
            }
        }

        // UI Binding Implementation
        private double _progressValue;
        public double ProgressValue 
        { 
            get => _progressValue; 
            set 
            {
                if (_progressValue != value)
                {
                    _progressValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowProgress));
                }
            } 
        }
        public bool ShowProgress => ProgressValue > 0;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public bool IsAvailableOnIptv { get; set; }
        public string SourceBadgeText => IsAvailableOnIptv ? "IPTV" : "";
        public bool ShowSourceBadge => IsAvailableOnIptv;

        public TmdbMovieResult TmdbInfo { get; set; } // Can populate later if needed

        // Technical Metadata (Probe Results)
        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);
        public bool IsProbing { get; set; }
        public bool? IsOnline { get; set; }
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        // Properties for UI Binding
        public string Type { get => Meta.Type; set => Meta.Type = value; }
        public string Year => Meta.ReleaseInfo;
        public string Banner => Meta.Background;
        public string Description => Meta.Description;
        public string? EpisodeSubtext { get; set; }

        public string Genres => (Meta.Genres != null && Meta.Genres.Count > 0) ? string.Join(", ", Meta.Genres) : "";
        
        // [IPTV Integration]
        public bool IsIptv { get; set; } = false;
        public string IMDbIdRaw { set => Meta.Id = value; }

        public bool HasPoster => IsPosterValid(PosterUrl);
        public bool HasNoPoster => !IsPosterValid(PosterUrl);

        public static bool IsPosterValid(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.Length < 10) return false; // Most valid image URLs are longer
            return true;
        }

        
        public string DisplaySubtext => IsContinueWatching 
            ? (IsSeries && !string.IsNullOrEmpty(EpisodeSubtext) ? EpisodeSubtext : "") 
            : (IsMovie ? "" : Genres); 
        
        // Helper
        public BitmapImage PosterBitmap => !string.IsNullOrEmpty(PosterUrl) ? new BitmapImage(new System.Uri(PosterUrl)) : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
