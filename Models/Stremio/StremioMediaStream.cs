using System;
using System.ComponentModel;

using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Helpers;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioMediaStream : IMediaStream, INotifyPropertyChanged
    {
        public StremioMeta Meta { get; set; }
        
        public StremioMediaStream() { Meta = new StremioMeta(); }
        
        private readonly System.Threading.Lock _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        public int PriorityScore { get => MetadataPriority; set => MetadataPriority = value; }
        public uint Fingerprint { get; set; }
        
        public StremioMediaStream(StremioMeta meta)
        {
            Meta = meta;
            TryPreEnrichFromCache();
        }

        private void TryPreEnrichFromCache()
        {
            if (Meta == null) return;
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
        public int Id 
        { 
            get 
            {
                if (int.TryParse(Meta?.Id, out var id)) return id;
                if (!string.IsNullOrEmpty(Meta?.Id)) return Meta.Id.GetHashCode();
                if (!string.IsNullOrEmpty(Meta?.Name)) return Meta.Name.GetHashCode();
                return 0;
            } 
        }
        public string? IMDbId => !string.IsNullOrEmpty(Meta?.Id) ? Meta.Id : string.Empty;

        public string Title { get => Meta?.Name ?? "Loading..."; set { if (Meta != null) Meta.Name = value; } }
        public string? SourceTitle { get => Title; set => Title = value; }

        // PosterUrl: uses override (set by async enrichment) if available, else Meta.Poster
        private string _overridePosterUrl;
        public string PosterUrl
        {
            get => _overridePosterUrl ?? Meta?.Poster;
            set
            {
                if (_overridePosterUrl != value)
                {
                    _overridePosterUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _overrideLogoUrl;
        public string LogoUrl
        {
            get => _overrideLogoUrl ?? Meta?.Logo;
            set
            {
                if (_overrideLogoUrl != value)
                {
                    _overrideLogoUrl = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Rating { get => string.IsNullOrEmpty(Meta?.Imdbrating) || Meta?.Imdbrating == "N/A" || Meta?.Imdbrating == "Unknown" ? "" : Meta.Imdbrating; set { if (Meta != null) { Meta.Imdbrating = value; OnPropertyChanged(); } } }
        public string StreamUrl { get; set; } = "";
        public string? BackdropUrl { get => Banner; set { if (Meta != null) { Meta.Background = value; OnPropertyChanged(nameof(Banner)); OnPropertyChanged(nameof(BackdropUrl)); OnPropertyChanged(nameof(LandscapeImageUrl)); } } }
        
        // Use Backdrop/Banner for Landscape cards if available, else fallback to Poster.
        public string LandscapeImageUrl => !string.IsNullOrEmpty(Banner) ? Banner : PosterUrl;

        public bool IsContinueWatching { get; set; }
        public bool IsNotContinueWatching => !IsContinueWatching;
        public bool IsMovie => Meta?.Type?.ToLower() == "movie";
        public bool IsSeries => Meta?.Type?.ToLower() == "series" || Meta?.Type?.ToLower() == "tv";

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

        private bool _isAvailableOnIptv;
        public bool IsAvailableOnIptv 
        { 
            get => _isAvailableOnIptv; 
            set 
            {
                if (_isAvailableOnIptv != value)
                {
                    _isAvailableOnIptv = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowSourceBadge));
                    OnPropertyChanged(nameof(SourceBadgeText));
                }
            }
        }
        public string SourceBadgeText => (IsAvailableOnIptv || IsIptv) ? "IPTV" : "";
        public bool IsIptvChecked { get; set; } = false;
        public bool ShowSourceBadge => IsAvailableOnIptv || IsIptv;

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
        public string Type { get => Meta?.Type ?? "movie"; set { if (Meta != null) Meta.Type = value; } }
        public string Year 
        { 
            get 
            {
                if (Meta == null) return "";
                // 1. Check Year field
                string y = TitleHelper.ExtractYear(Meta.Year);
                if (!string.IsNullOrEmpty(y)) return y;

                // 2. Check Releaseinfo field
                y = TitleHelper.ExtractYear(Meta.Releaseinfo);
                if (!string.IsNullOrEmpty(y)) return y;

                // 3. Check Released ISO date
                y = TitleHelper.ExtractYear(Meta.Released);
                if (!string.IsNullOrEmpty(y)) return y;

                // 4. Fallback to Title
                return TitleHelper.ExtractYear(Meta.Name) ?? "";
            }
            set { if (Meta != null) Meta.Releaseinfo = value; } 
        }
        public string Banner => Meta?.Background ?? "";
        public string Description { get => Meta?.Description ?? ""; set { if (Meta != null) { Meta.Description = value; OnPropertyChanged(); } } }

        private string? _overrideCast;
        public string? Cast { get => _overrideCast ?? (Meta?.Cast != null && Meta.Cast.Count > 0 ? string.Join(", ", Meta.Cast) : null); set { if (_overrideCast != value) { _overrideCast = value; OnPropertyChanged(); } } }

        private string? _overrideDirector;
        public string? Director { get => _overrideDirector ?? (Meta?.Director != null && Meta.Director.Count > 0 ? string.Join(", ", Meta.Director) : null); set { if (_overrideDirector != value) { _overrideDirector = value; OnPropertyChanged(); } } }

        private string? _trailerUrl; // For manual enrichment retention
        public string? TrailerUrl 
        { 
            get
            {
                if (!string.IsNullOrWhiteSpace(_trailerUrl)) return _trailerUrl;

                string? trailer = Meta?.Trailers?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Source))?.Source;
                if (!string.IsNullOrWhiteSpace(trailer))
                {
                    return trailer;
                }

                trailer = Meta?.TrailerStreams?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.YtId))?.YtId;
                if (!string.IsNullOrWhiteSpace(trailer))
                {
                    return trailer;
                }

                trailer = Meta?.AppExtras?.Trailer;
                return string.IsNullOrWhiteSpace(trailer) ? null : trailer;
            }
            set 
            { 
                if (_trailerUrl != value)
                {
                    _trailerUrl = value;
                    
                    // Also sync back to Meta.Trailers if possible
                    if (Meta != null && !string.IsNullOrEmpty(value))
                    {
                        if (Meta.Trailers == null) Meta.Trailers = new System.Collections.Generic.List<StremioMetaTrailer>();
                        var existing = Meta.Trailers.FirstOrDefault();
                        if (existing != null) existing.Source = value;
                        else Meta.Trailers.Add(new StremioMetaTrailer { Source = value, Type = "Trailer" });
                    }
                    OnPropertyChanged();
                }
            } 
        }

        public string? Genres 
        { 
            get => (Meta.Genres != null && Meta.Genres.Count > 0) ? string.Join(", ", Meta.Genres) : "";
            set { if (Meta != null && value != null) { Meta.Genres = value.Split(", ").ToList(); OnPropertyChanged(); } } 
        }

        public string? EpisodeSubtext { get; set; }
        public string? SeriesName { get; set; }
        
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
        [JsonIgnore]
        public BitmapImage PosterBitmap => !string.IsNullOrEmpty(PosterUrl) ? new BitmapImage(new System.Uri(PosterUrl)) : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            var queue = App.MainWindow?.DispatcherQueue;
            if (queue == null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return;
            }

            if (queue.HasThreadAccess)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            else
            {
                queue.TryEnqueue(() =>
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                });
            }
        }

        [JsonIgnore]
        public Models.Metadata.MetadataContext CurrentEnrichmentLevel { get; set; } = Models.Metadata.MetadataContext.Discovery;

        public void UpdateFromUnified(Models.Metadata.UnifiedMetadata? meta)
        {
            if (meta == null) return;

            lock (_metaLock)
            {
                // Source-Based Priority Protection
                bool isDowngrade = meta.PriorityScore < this.MetadataPriority;

                // Sync with backfillOnly = true if incoming data has lower priority
                Models.Metadata.MetadataSync.Sync(this, meta, isDowngrade);

                // Update current priority if we successfully enriched or it's equal priority
                if (!isDowngrade)
                {
                    this.MetadataPriority = meta.PriorityScore;
                    this.CurrentEnrichmentLevel = meta.MaxEnrichmentContext;
                }
            }

            // High-priority IPTV fields (if specialized)
            if (meta.IsAvailableOnIptv)
            {
                this.IsAvailableOnIptv = true;
                if (string.IsNullOrEmpty(this.StreamUrl)) this.StreamUrl = meta.StreamUrl;
            }
        }

        public void NotifyMetadataUpdated()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Year));
            OnPropertyChanged(nameof(PosterUrl));
            OnPropertyChanged(nameof(HasPoster));
            OnPropertyChanged(nameof(HasNoPoster));
            OnPropertyChanged(nameof(LandscapeImageUrl));
            OnPropertyChanged(nameof(Banner));
            OnPropertyChanged(nameof(BackdropUrl));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Rating));
            OnPropertyChanged(nameof(TrailerUrl));
        }
    }
}
