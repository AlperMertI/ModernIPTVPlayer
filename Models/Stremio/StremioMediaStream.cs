using System;
using System.ComponentModel;
using System.Threading;
using System.Collections.Generic;
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
    public partial class StremioMediaStream : IMediaStream, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // PROJECT ZERO: Centralized update bus for zero-allocation UI refreshes.
        public static event Action<string>? OnMetadataUpdated;

        private bool _isPinned;
        /// <summary>
        /// Prevents the object from being recycled by the VirtualCollection.
        /// Essential for stability in ExpandedCard and Hero transitions.
        /// </summary>
        public void Pin() => _isPinned = true;
        public void Unpin() => _isPinned = false;
        public bool IsPinned => _isPinned;

        private int _updateLevel;
        private bool IsUpdating => _updateLevel > 0;

        /// <summary>
        /// Suspends UI notifications for multiple property changes.
        /// </summary>
        public void BeginUpdate() => Interlocked.Increment(ref _updateLevel);

        private long _lastRefreshTime;
        /// <summary>
        /// Resumes UI notifications and fires a single global refresh signal.
        /// </summary>
        public void EndUpdate()
        {
            if (Interlocked.Decrement(ref _updateLevel) == 0)
            {
                // [THROTTLE] Only fire if substantial time has passed or it's the final update.
                // This prevents "UI Thread floods" during rapid successive Sync() calls.
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _lastRefreshTime > 150) // Increased threshold for stability
                {
                    NotifyMetadataUpdated();
                    _lastRefreshTime = now;
                }
            }
        }

        public virtual void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            if (IsUpdating) return;

            var queue = App.MainWindow?.DispatcherQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
            else
            {
                queue.TryEnqueue(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
            }
        }

        private StremioMeta _meta;
        public StremioMeta Meta 
        { 
            get => _meta; 
            set 
            { 
                if (_meta != value) 
                { 
                    _meta = value; 
                    ResetEnrichmentState();
                    OnPropertyChanged(); 
                    OnPropertyChanged(string.Empty); // Broad refresh
                } 
            } 
        }

        private void ResetEnrichmentState()
        {
            TrailerUrl = null;
            _sourceAddon = null;
            MetadataPriority = 0;
            // LogoLoadFailed = false; // [FIX] Don't reset logo failure state, it will be re-evaluated by UI if URL changes.
            // Add other transient fields here if necessary
        }
        
        public StremioMediaStream() 
        { 
            Meta = new StremioMeta(); 
            OnMetadataUpdated += HandleMetadataUpdate;
        }

        public StremioMediaStream(StremioMeta meta) : this()
        {
            Meta = meta;
            TryPreEnrichFromCache();
        }

        private void HandleMetadataUpdate(string id)
        {
            if (Meta?.Id == id)
            {
                NotifyMetadataUpdated();
            }
        }

        private readonly System.Threading.Lock _metaLock = new();
        public int MetadataPriority { get; set; } = 0;
        public int PriorityScore { get => MetadataPriority; set => MetadataPriority = value; }
        public uint Fingerprint { get; set; }
        
        private void TryPreEnrichFromCache()
        {
            if (Meta == null) return;
            if (string.IsNullOrEmpty(Meta.Background) && !string.IsNullOrEmpty(Meta.Id))
            {
                try
                {
                    string typeKey = IsSeries ? "tv" : "movie";
                    string idKey = Meta.Id.StartsWith("tt") ? $"{typeKey}_id_{Meta.Id}" : null;
                    
                    if (idKey != null)
                    {
                        var cached = Services.TmdbCacheService.Instance.Get<TmdbMovieResult>(idKey);
                        if (cached != null && !string.IsNullOrEmpty(cached.BackdropPath))
                        {
                            Meta.Background = $"https://image.tmdb.org/t/p/w1280{cached.BackdropPath}";
                        }
                    }
                }
                catch { }
            }
        }

        private string? _sourceAddon;
        public string? SourceAddon 
        { 
            get 
            {
                if (string.IsNullOrEmpty(_sourceAddon))
                {
                    // System.Diagnostics.Debug.WriteLine($"[STREAM_DEBUG] SourceAddon MISSING for: {Title}");
                }
                return _sourceAddon; 
            }
            set => _sourceAddon = value; 
        }
        public bool LogoLoadFailed { get; set; }
        public int SourceIndex { get; set; } = 999;

        // IMediaStream Implementation
        public int Id 
        { 
            get 
            {
                if (int.TryParse(Meta?.Id, out var id)) return id;
                if (!string.IsNullOrEmpty(Meta?.Id)) return Meta.Id.GetHashCode();
                return 0;
            } 
        }
        public string? IMDbId => Meta?.Id ?? string.Empty;

        public string Title { get => Meta?.Name ?? "Loading..."; set { if (Meta != null) Meta.Name = value; } }
        public string? SourceTitle { get => Title; set => Title = value; }

        private string _overridePosterUrl;
        public string PosterUrl
        {
            get => _overridePosterUrl ?? Meta?.Poster;
            set { if (_overridePosterUrl != value) { _overridePosterUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(PosterBitmap)); } }
        }

        public BitmapImage? PosterBitmap => string.IsNullOrEmpty(PosterUrl) ? null : SharedImageManager.GetOptimizedImage(PosterUrl, targetWidth: 150);
        public BitmapImage? BannerBitmap => string.IsNullOrEmpty(Banner) ? null : SharedImageManager.GetOptimizedImage(Banner, targetWidth: 900);

        private string _overrideLogoUrl;
        public string LogoUrl
        {
            get => _overrideLogoUrl ?? Meta?.Logo;
            set { if (_overrideLogoUrl != value) { _overrideLogoUrl = value; OnPropertyChanged(); } }
        }

        public string Rating 
        { 
            get => (Meta?.Imdbrating > 0) ? Meta.Imdbrating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : ""; 
            set { if (Meta != null && double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r)) Meta.Imdbrating = r; OnPropertyChanged(); }
        }
        public string StreamUrl { get; set; } = "";
        public string? BackdropUrl { get => Banner; set { if (Meta != null) { Meta.Background = value; OnPropertyChanged(nameof(Banner)); OnPropertyChanged(nameof(BackdropUrl)); OnPropertyChanged(nameof(LandscapeImageUrl)); OnPropertyChanged(nameof(BannerBitmap)); } } }
        
        public string LandscapeImageUrl => !string.IsNullOrEmpty(Banner) ? Banner : PosterUrl;

        public bool IsContinueWatching { get; set; }
        public bool IsNotContinueWatching => !IsContinueWatching;
        public bool IsMovie => Meta?.Type?.ToLower() == "movie";
        public bool IsSeries => Meta?.Type?.ToLower() == "series" || Meta?.Type?.ToLower() == "tv";

        private double _progressValue;
        public double ProgressValue 
        { 
            get => _progressValue; 
            set { if (_progressValue != value) { _progressValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowProgress)); } } 
        }
        public bool ShowProgress => ProgressValue > 0;
        public string BadgeText => "";
        public bool ShowBadge => false;

        private bool _isAvailableOnIptv;
        public bool IsAvailableOnIptv 
        { 
            get => _isAvailableOnIptv; 
            set { if (_isAvailableOnIptv != value) { _isAvailableOnIptv = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSourceBadge)); OnPropertyChanged(nameof(SourceBadgeText)); } }
        }
        public string SourceBadgeText => (IsAvailableOnIptv || IsIptv) ? "IPTV" : "";
        public bool IsIptvChecked { get; set; } = false;
        public bool ShowSourceBadge => IsAvailableOnIptv || IsIptv;

        public TmdbMovieResult TmdbInfo { get; set; }

        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);
        public bool IsProbing { get; set; }
        public bool? IsOnline { get; set; }
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        public string Type { get => Meta?.Type ?? "movie"; set { if (Meta != null) Meta.Type = value; } }
        public string Year 
        { 
            get 
            {
                if (Meta == null) return "";
                
                var yearSpan = TitleHelper.ExtractYear(Meta.Year.AsSpan());
                if (yearSpan.IsEmpty) yearSpan = TitleHelper.ExtractYear(Meta.Releaseinfo.AsSpan());
                if (yearSpan.IsEmpty) yearSpan = TitleHelper.ExtractYear(Meta.Released.AsSpan());
                if (yearSpan.IsEmpty) yearSpan = TitleHelper.ExtractYear(Meta.Name.AsSpan());
                
                return yearSpan.IsEmpty ? "" : yearSpan.ToString();
            }
            set { if (Meta != null) { Meta.Year = value; OnPropertyChanged(); } }
        }
        public string Banner => Meta?.Background ?? "";
        public string Description 
        { 
            get 
            {
                if (Meta == null) return "";
                if (!string.IsNullOrEmpty(Meta.Description) && !Services.Metadata.MetadataProvider.IsPlaceholderOverview(Meta.Description)) return Meta.Description;
                return Meta.Description ?? "";
            }
            set { if (Meta != null) { Meta.Description = value; OnPropertyChanged(); } } 
        }
        public string Overview { get => Description; set => Description = value; }

        private string? _overrideCast;
        public string? Cast { get => _overrideCast ?? (Meta?.Cast != null && Meta.Cast.Count > 0 ? string.Join(", ", Meta.Cast) : null); set { if (_overrideCast != value) { _overrideCast = value; OnPropertyChanged(); } } }

        private string? _overrideDirector;
        public string? Director { get => _overrideDirector ?? (Meta?.Director != null && Meta.Director.Count > 0 ? string.Join(", ", Meta.Director) : null); set { if (_overrideDirector != value) { _overrideDirector = value; OnPropertyChanged(); } } }

        public string? TrailerUrl 
        { 
            get 
            {
                // 1. Check local field (Enriched via Sync)
                if (!string.IsNullOrEmpty(field)) return field;

                // 2. Fallback to Catalog Data
                return Meta?.Trailers?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Source))?.Source ?? 
                       Meta?.TrailerStreams?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.YtId))?.YtId ?? 
                       Meta?.AppExtras?.Trailer;
            }
            set 
            { 
                if (field != value) 
                { 
                    field = value; 
                    OnPropertyChanged(); 
                } 
            } 
        } = null;

        public string? Genres { get => Meta?.Genres ?? ""; set { if (Meta != null) { Meta.Genres = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySubtext)); } } }

        public string? EpisodeSubtext { get; set; }
        public string? SeriesName { get; set; }
        public bool IsIptv { get; set; } = false;

        public bool HasPoster => IsPosterValid(PosterUrl);
        public bool HasNoPoster => !IsPosterValid(PosterUrl);

        public static bool IsPosterValid(string? url) => !string.IsNullOrWhiteSpace(url) && !url.Equals("null", StringComparison.OrdinalIgnoreCase) && url.Length > 10;
        
        public string DisplaySubtext => IsContinueWatching 
            ? (IsSeries && !string.IsNullOrEmpty(EpisodeSubtext) ? EpisodeSubtext : "") 
            : (IsMovie ? "" : Genres); 

        private bool _isSilenced = false;
        public void UpdateFromUnified(Models.Metadata.UnifiedMetadata meta)
        {
            if (meta == null) return;
            lock (_metaLock)
            {
                BeginUpdate();
                bool isDowngrade = meta.PriorityScore < this.MetadataPriority;
                Models.Metadata.MetadataSync.Sync(this, meta, isDowngrade);
                
                if (!isDowngrade)
                {
                    this.MetadataPriority = meta.PriorityScore;
                }
                EndUpdate();
            }
        }

        /// <summary>
        /// PROJECT ZERO: Batched UI Refresh.
        /// Notifies the UI that multiple properties have changed in a single dispatcher task.
        /// This is the key to maintaining 60FPS during background enrichment.
        /// </summary>
        private bool _isRefreshPending;
        /// <summary>
        /// PROJECT ZERO: Batched UI Refresh.
        /// Notifies the UI that multiple properties have changed in a single dispatcher task.
        /// This is the key to maintaining 60FPS during background enrichment.
        /// </summary>
        public void NotifyMetadataUpdated()
        {
            if (_isRefreshPending) return;

            var queue = App.MainWindow?.DispatcherQueue;
            if (queue == null)
            {
                OnPropertyChanged(string.Empty);
                return;
            }

            _isRefreshPending = true;
            queue.TryEnqueue(() => {
                _isRefreshPending = false;
                OnPropertyChanged(string.Empty);
            });
        }

        ~StremioMediaStream()
        {
            OnMetadataUpdated -= HandleMetadataUpdate;
        }
    }
}
