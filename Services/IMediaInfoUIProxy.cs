using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Controls;
using Microsoft.UI.Dispatching;

namespace ModernIPTVPlayer.Services
{
    public enum PageLoadState
    {
        Initial,      // Page created, no layout yet
        LayoutReady,  // First SizeChanged triggered
        Loading,     // Data loading (shimmer shown)
        Revealing,   // Data arrived, animation starting
        Ready        // Everything complete
    }

    public enum MediaDetailPanelMode
    {
        None,
        Episodes,
        Sources
    }

    public enum MediaContentKind
    {
        Unknown,
        Movie,
        Series,
        Live
    }

    public enum PanelChangeReason
    {
        Reset,
        NavigationDefault,
        MovieAutoSources,
        SeriesDefaultEpisodes,
        EpisodeSelected,
        EpisodeDeselected,
        EpisodeRequired,
        SourcesRequested,
        SourcesClosed,
        BackToEpisodes,
        SourceFetch,
        SourceCache,
        NoSources
    }

    /// <summary>
    /// Defines the UI interaction surface required for committing metadata updates.
    /// This decouples the CommitService from the full Page implementation.
    /// </summary>
    public interface IMediaInfoUIProxy
    {
        DispatcherQueue DispatcherQueue { get; }
        ModernIPTVPlayer.Controls.MediaIdentityControl IdentityControl { get; }
        TextBlock StickyTitle { get; }
        TextBlock OverviewText { get; }
        TextBlock YearText { get; }
        TextBlock GenresText { get; }
        TextBlock RuntimeText { get; }
        TextBlock PlayButtonText { get; }
        TextBlock StickyPlayButtonText { get; }
        TextBlock PlayButtonSubtext { get; }
        TextBlock StickyPlayButtonSubtext { get; }
        TextBlock SourceAttributionText { get; }
        Button PlayButton { get; }
        Button RestartButton { get; }
        Button TrailerButton { get; }
        Button DownloadButton { get; }
        Button CopyLinkButton { get; }
        ModernIPTVPlayer.Models.Metadata.UnifiedMetadata Metadata { set; }
        
        string StreamUrl { get; set; }
        bool IsLogoImageLoaded { get; }
        bool IsLogoPending { get; set; }
        bool IsLogoReady { get; set; }
        bool IsLogoFallbackActive { get; set; }
        string CurrentLogoUrl { get; set; }
        DateTime NavigationStartTime { get; }
        double ActualWidth { get; }

        void SetLoadState(PageLoadState state);
        void SyncLayout();
        void ApplyOverviewTextLayout(bool isWide);
        void StartPrebuffering(string url, double position = 0);
        void RefreshAllAddonActiveFlags();
        void SyncAddonSelectionToActive();
        void UpdateWatchlistState(bool? state = null);
        void SyncActionButtons(HistoryItem history);
        void AddBackdropToSlideshow(string url);
        void StartBackgroundSlideshow(List<string> urls);
        void ApplyHeroSeedImage(string url, string reason);
        void PlayButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e);
        void MatchTitleSkeletonToContent();
        
        Task PlayStremioContent(string videoId, bool showGlobalLoading = false, bool autoPlay = true, double startSeconds = -1);
        Task PerformHandoverAndNavigate(string url, string title, string id = null, string parentId = null, string seriesName = null, int season = 0, int episode = 0, double startSeconds = -1, string posterUrl = null, string type = null, string backdropUrl = null);
        void OpenSourcesPanel(PanelChangeReason reason);
        void OpenEpisodesPanel(PanelChangeReason reason);
        void ShowActionFeedback(string title, string subtitle, object target = null);
        string ResolveBestContentId(string id);
        string GetCurrentBackdrop();
        
        Task PopulateCastAndDirectors(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata);
        Task LoadSeriesDataAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata);
        Task UpdateTechnicalBadgesAsync(string url);
        
        Task PlayTrailer(string videoKey);
        Task DownloadSingle();
        Task DownloadSeason();
        void CopyToClipboard(string text);
        void SetWatchlistIcon(bool isInList, bool animate);
    }
}
