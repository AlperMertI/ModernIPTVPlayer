using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Tmdb;
using Microsoft.UI.Dispatching;
using System.Diagnostics;

namespace ModernIPTVPlayer.Services.Metadata
{

    /// <summary>
    /// Manages the atomic commitment of metadata snapshots to the UI.
    /// Uses incremental section diffing for all commits (including initial) to prevent redundant UI updates.
    /// </summary>
    public class MediaInfoCommitService
    {
        private readonly IMediaInfoUIProxy _ui;
        private readonly Dictionary<string, string> _sectionKeys = new(StringComparer.Ordinal);
        private ModernIPTVPlayer.Models.Metadata.UnifiedMetadata? _lastMetadata;
        private IMediaStream? _lastItem;
        private bool _deferVisuals;
        private bool _techBadgesInitialized;
        
        public bool HasCommittedEpisodes { get; private set; }

        public MediaInfoCommitService(IMediaInfoUIProxy ui)
        {
            _ui = ui;
        }

        public void Reset()
        {
            _sectionKeys.Clear();
            HasCommittedEpisodes = false;
            _lastMetadata = null;
            _lastItem = null;
            _deferVisuals = false;
            _techBadgesInitialized = false;
        }

        public void RetryIdentityReveal()
        {
            if (_sectionKeys.Count > 0 || _lastMetadata == null || _lastItem == null) return;
            _ = CommitAsync(_lastMetadata, _lastItem);
        }

        public async Task<bool> CommitAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata? metadata, IMediaStream item, bool deferVisuals = false)
        {
            if (metadata == null || item == null) return false;

            _deferVisuals = deferVisuals;

            // If we are on a background thread, marshal to UI thread and await
            if (!_ui.DispatcherQueue.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource<bool>();
                bool ok = _ui.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        bool result = await CommitAsync(metadata, item, deferVisuals);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ERR-COMMIT] Marshaled CommitAsync failed: {ex.Message}");
                        tcs.SetException(ex);
                    }
                });

                if (!ok) 
                {
                    System.Diagnostics.Debug.WriteLine("[ERR-COMMIT] Failed to enqueue CommitAsync to DispatcherQueue");
                    return false;
                }
                return await tcs.Task;
            }

            _lastMetadata = metadata;
            _lastItem = item;
            _ui.Metadata = metadata; // Sync to Page
            
            PrepareMetadataDefaults(metadata, item);

            try
            {
                bool changed = false;
                var sectionSw = Stopwatch.StartNew();

                if (CommitSection("identity", $"{metadata.IdentityKey}|{item.Title}"))
                {
                    ApplyIdentity(metadata, item);
                    changed = true;
                    NotifySectionIfNotDeferred("identity");
                }

                if (CommitSection("details", metadata.DetailsKey))
                {
                    ApplyDetails(metadata);
                    changed = true;
                    NotifySectionIfNotDeferred("overview");
                }

                if (CommitSection("actions", metadata.ActionsKey))
                {
                    ApplyActions(metadata, item);
                    changed = true;
                    NotifySectionIfNotDeferred("actionbar");
                }

                if (CommitSection("people", metadata.PeopleKey))
                {
                    await _ui.PopulateCastAndDirectors(metadata);
                    changed = true;
                    NotifySectionIfNotDeferred("cast");
                    NotifySectionIfNotDeferred("director");
                }

                if (HasEpisodes(metadata) && CommitSection("episodes", metadata.EpisodesKey))
                {
                    var epSw = Stopwatch.StartNew();
                    await _ui.LoadSeriesDataAsync(metadata);
                    epSw.Stop();
                    System.Diagnostics.Debug.WriteLine($"[NAV-TIMING] LoadSeriesDataAsync (episodes): {epSw.ElapsedMilliseconds}ms");
                    HasCommittedEpisodes = true;
                    changed = true;
                }

                if (CommitSection("backdrop", metadata.BackdropKey))
                {
                    ApplyBackdrop(metadata);
                    changed = true;
                }

                if (CommitSection("attribution", metadata.AttributionKey))
                {
                    ApplyAttribution(metadata);
                    changed = true;
                }

                // Tech badges setup — runs once when a stream URL is available
                if (!_techBadgesInitialized && !string.IsNullOrEmpty(_ui.StreamUrl))
                {
                    _techBadgesInitialized = true;
                    _ui.ShowTechBadgesShimmer();
                    _ = _ui.UpdateTechnicalBadgesAsync(_ui.StreamUrl);
                }

                sectionSw.Stop();
                System.Diagnostics.Debug.WriteLine($"[NAV-TIMING] Section commits (all sections): {sectionSw.ElapsedMilliseconds}ms");

                if (changed && !_deferVisuals)
                {
                    _ui.SyncLayout();
                }
                else if (changed && _deferVisuals)
                {
                    _ui.SyncLayout();
                }

                return changed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERR-COMMIT] CommitAsync logic failure: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Flushes any deferred visual transitions after Page.Loaded has fired.
        /// Called by the orchestrator once the visual tree is ready.
        /// </summary>
        public void FlushDeferredVisuals()
        {
            if (!_deferVisuals || _sectionKeys.Count == 0) return;
            _deferVisuals = false;
            _ui.SyncLayout();
        }

        private static bool HasPrimarySource(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if (metadata == null) return false;
            string sourceInfo = metadata.MetadataSourceInfo ?? "";
            
            bool isPrimaryDetail = sourceInfo.Contains("(Primary)", StringComparison.OrdinalIgnoreCase) || 
                                   sourceInfo.Equals("TMDB", StringComparison.OrdinalIgnoreCase);
            
            bool isPrimaryCatalogSeed = sourceInfo.Contains("Catalog Seed", StringComparison.OrdinalIgnoreCase) &&
                                        !string.IsNullOrWhiteSpace(metadata.PrimaryMetadataAddonUrl) &&
                                        string.Equals(metadata.PrimaryMetadataAddonUrl, metadata.CatalogSourceAddonUrl, StringComparison.OrdinalIgnoreCase);

            return isPrimaryDetail || isPrimaryCatalogSeed;
        }

        private bool CommitSection(string name, string nextKey)
        {
            if (_sectionKeys.TryGetValue(name, out var current) && string.Equals(current, nextKey, StringComparison.Ordinal))
            {
                return false;
            }

            _sectionKeys[name] = nextKey;
            return true;
        }

        private void NotifySectionIfNotDeferred(string sectionName)
        {
            if (!_deferVisuals)
            {
                _ui.NotifySectionDataReady(sectionName);
            }
        }

        private void PrepareMetadataDefaults(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            if (metadata == null || item == null) return;
            if (string.IsNullOrWhiteSpace(metadata.PosterUrl)) metadata.PosterUrl = item.PosterUrl;
            if (string.IsNullOrWhiteSpace(metadata.BackdropUrl))
            {
                metadata.BackdropUrl = (item as Models.Stremio.StremioMediaStream)?.Meta?.Background ?? item.BackdropUrl;
            }
        }

        private void ApplyIdentity(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            string navSeedTitle = item.Title?.Trim() ?? "";
            string title = string.IsNullOrEmpty(metadata.Title) || metadata.Title == "Unknown"
                ? (string.IsNullOrEmpty(navSeedTitle) ? "Unknown Content" : navSeedTitle)
                : metadata.Title;

            _ui.IdentityControl?.SetTitle(title);
            if (_ui.StickyTitle != null) _ui.StickyTitle.Text = title;

            bool hasLogo = !string.IsNullOrWhiteSpace(metadata.LogoUrl);
            if (hasLogo)
            {
                _ui.IsLogoPending = true;
                _ui.IsLogoFallbackActive = false;
                _ui.IdentityControl?.SetLogo(metadata.LogoUrl);
            }
            else
            {
                _ui.CurrentLogoUrl = null;
                _ui.IsLogoReady = false;
                _ui.IsLogoPending = ShouldWaitForLogo(metadata);
                _ui.IsLogoFallbackActive = !_ui.IsLogoPending;
                _ui.IdentityControl?.SetLogo((string)null);
            }

            _ui.IdentityControl?.SetSuperTitle(metadata.SubTitle);
        }

        private void ApplyDetails(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if (_ui.OverviewText != null) _ui.OverviewText.Text = !string.IsNullOrEmpty(metadata.Overview) ? metadata.Overview : "Açıklama mevcut değil.";
            if (_ui.YearText != null) _ui.YearText.Text = metadata.Year?.Split('-')[0] ?? "";
            if (_ui.GenresText != null)
            {
                _ui.GenresText.Text = metadata.Genres;
                _ui.GenresText.Visibility = !string.IsNullOrEmpty(_ui.GenresText.Text) ? Visibility.Visible : Visibility.Collapsed;
                if (_ui.GenresText.Parent is Grid pGrid) pGrid.Visibility = _ui.GenresText.Visibility;
            }
            if (_ui.RuntimeText != null) _ui.RuntimeText.Text = metadata.IsSeries ? "Dizi" : metadata.Runtime;
            _ui.ApplyOverviewTextLayout(_ui.ActualWidth >= 1100); // threshold
        }

        private void ApplyActions(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            if (item is Models.Stremio.StremioMediaStream sms && metadata.IsAvailableOnIptv)
            {
                sms.IsAvailableOnIptv = true;
            }

            if (metadata.IsAvailableOnIptv)
            {
                item.IsAvailableOnIptv = true;
                if (string.IsNullOrEmpty(item.StreamUrl)) item.StreamUrl = metadata.StreamUrl;
            }

            if (_ui.PlayButton != null) _ui.PlayButton.Visibility = Visibility.Visible;
            if (_ui.TrailerButton != null) _ui.TrailerButton.Visibility = !string.IsNullOrEmpty(metadata.TrailerUrl) ? Visibility.Visible : Visibility.Collapsed;
            if (_ui.DownloadButton != null) _ui.DownloadButton.Visibility = Visibility.Visible;
            if (_ui.CopyLinkButton != null) _ui.CopyLinkButton.Visibility = Visibility.Visible;

            string metadataId = metadata.MetadataId;
            string imdbId = metadata.ImdbId ?? (item as Models.Stremio.StremioMediaStream)?.Meta?.ImdbId;
            HistoryItem history = null;

            if (metadata.IsSeries)
            {
                history = HistoryManager.Instance.GetLastWatchedEpisode(metadataId ?? item.Id.ToString());
                if (history == null && !string.IsNullOrEmpty(imdbId))
                {
                    history = HistoryManager.Instance.GetLastWatchedEpisode(imdbId);
                }
                if (history == null)
                {
                    history = HistoryManager.Instance.GetHistoryByTitle(metadata.Title, "series");
                }

                _ui.SyncActionButtons(history);

                if (history != null && !history.IsFinished && !string.IsNullOrEmpty(history.StreamUrl))
                {
                    _ui.StreamUrl = history.StreamUrl;
                    _ui.StartPrebuffering(_ui.StreamUrl, history.Position);
                }
            }
            else
            {
                history = HistoryManager.Instance.GetProgress(metadataId ?? item.Id.ToString());
                if (history == null && !string.IsNullOrEmpty(imdbId))
                {
                    history = HistoryManager.Instance.GetProgress(imdbId);
                }
                if (history == null)
                {
                    history = HistoryManager.Instance.GetHistoryByTitle(metadata.Title, "movie");
                }

                _ui.SyncActionButtons(history);

                if (history != null && history.Position > 0 && !history.IsFinished)
                {
                    _ui.StreamUrl = history.StreamUrl;
                    _ui.StartPrebuffering(_ui.StreamUrl, history.Position);
                    _ui.RefreshAllAddonActiveFlags();
                    _ui.SyncAddonSelectionToActive();
                }
                else if (item is LiveStream liveS && !string.IsNullOrEmpty(liveS.StreamUrl))
                {
                    _ui.StreamUrl = liveS.StreamUrl;
                    _ui.StartPrebuffering(_ui.StreamUrl);
                }

                // Request sources panel to open when the page reaches a stable state.
                // If the page is still loading or revealing, the request is deferred.
                _ui.RequestPanelOpen(MediaDetailPanelMode.Sources, PanelChangeReason.SourceCache);
            }

            _ui.UpdateWatchlistState();
        }



        private static string BuildRemainingText(HistoryItem history)
        {
            if (history == null || history.Duration <= 0) return "";
            double remainingSeconds = history.Duration - history.Position;
            if (remainingSeconds < 60) return "";

            var remaining = TimeSpan.FromSeconds(remainingSeconds);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı"
                : $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}dk Kaldı";
        }

        private void ApplyBackdrop(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            // [Senior] Prime with Poster instantly if we have one (likely in memory cache)
            if (!string.IsNullOrEmpty(metadata.PosterUrl))
            {
                _ui.ApplyHeroSeedImage(metadata.PosterUrl, "poster-prime");
            }

            if (!string.IsNullOrEmpty(metadata.BackdropUrl))
            {
                _ui.AddBackdropToSlideshow(metadata.BackdropUrl);
                _ui.ApplyHeroSeedImage(metadata.BackdropUrl, "backdrop");
            }
            
            if (metadata.BackdropUrls != null && metadata.BackdropUrls.Count > 0)
            {
                _ui.StartBackgroundSlideshow(metadata.BackdropUrls);
            }
        }

        private void ApplyAttribution(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if (_ui.SourceAttributionText == null) return;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.DataSource) && metadata.DataSource != "Unknown") parts.Add(metadata.DataSource);
            if (!string.IsNullOrWhiteSpace(metadata.MetadataSourceInfo) && metadata.MetadataSourceInfo != "Unknown")
            {
                string cleanMeta = metadata.MetadataSourceInfo.Replace(" (Primary)", "").Trim();
                if (!parts.Any(p => p.Contains(cleanMeta, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(metadata.MetadataSourceInfo);
                }
            }

            var finalParts = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            _ui.SourceAttributionText.Text = finalParts.Count > 0 ? string.Join(" + ", finalParts) : "Unknown";
        }

        private static bool ShouldWaitForLogo(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if (metadata == null || !string.IsNullOrWhiteSpace(metadata.LogoUrl)) return false;

            string sourceInfo = metadata.MetadataSourceInfo ?? "";
            bool isCatalogSeed = sourceInfo.Contains("Catalog Seed", StringComparison.OrdinalIgnoreCase);
            bool hasPrimaryIdentity = HasPrimarySource(metadata);
            bool detailSweepFinished = metadata.CheckedFields.HasFlag(MetadataField.Logo);

            return hasPrimaryIdentity && isCatalogSeed && !detailSweepFinished;
        }

        private static bool HasEpisodes(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            return metadata.IsSeries &&
                   metadata.Seasons != null &&
                   metadata.Seasons.Any(s => s.Episodes != null && s.Episodes.Count > 0);
        }
    }
}
