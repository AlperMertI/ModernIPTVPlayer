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

namespace ModernIPTVPlayer.Services.Metadata
{

    /// <summary>
    /// Manages the atomic commitment of metadata snapshots to the UI.
    /// Handles change detection (diffing) to prevent redundant UI updates.
    /// </summary>
    public class MediaInfoCommitService
    {
        private const int IdentityGateTimeoutMs = 3500;
        private readonly IMediaInfoUIProxy _ui;
        private readonly Dictionary<string, string> _sectionKeys = new(StringComparer.Ordinal);
        private bool _hasInitialCommit;
        private ModernIPTVPlayer.Models.Metadata.UnifiedMetadata? _lastMetadata;
        private IMediaStream? _lastItem;
        
        public bool HasCommittedEpisodes { get; private set; }

        public MediaInfoCommitService(IMediaInfoUIProxy ui)
        {
            _ui = ui;
        }

        public void Reset()
        {
            _sectionKeys.Clear();
            _hasInitialCommit = false;
            HasCommittedEpisodes = false;
            _lastMetadata = null;
            _lastItem = null;
        }

        public void RetryIdentityReveal()
        {
            if (_hasInitialCommit || _lastMetadata == null || _lastItem == null) return;
            _ = CommitAsync(_lastMetadata, _lastItem);
        }

        public async Task<bool> CommitAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata? metadata, IMediaStream item, bool forceInitialCommit = false)
        {
            if (metadata == null || item == null) return false;

            // If we are on a background thread, marshal to UI thread and await
            if (!_ui.DispatcherQueue.HasThreadAccess)
            {
                var tcs = new TaskCompletionSource<bool>();
                bool ok = _ui.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        bool result = await CommitAsync(metadata, item, forceInitialCommit);
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
                if (!_hasInitialCommit)
                {
                    if (!forceInitialCommit && !IsIdentityAuthorityReady(metadata))
                    {
                        return false;
                    }

                    await ApplyInitialCommitAsync(metadata, item);
                    _hasInitialCommit = true;
                    CaptureAll(metadata, item);

                    if (HasEpisodes(metadata))
                    {
                        await _ui.LoadSeriesDataAsync(metadata);
                        HasCommittedEpisodes = true;
                    }

                    return true;
                }

                bool changed = false;

                if (CommitSection("identity", BuildIdentityKey(metadata, item)))
                {
                    ApplyIdentity(metadata, item);
                    changed = true;
                }

                if (CommitSection("details", BuildDetailsKey(metadata)))
                {
                    ApplyDetails(metadata);
                    changed = true;
                }

                if (CommitSection("actions", BuildActionsKey(metadata)))
                {
                    ApplyActions(metadata, item);
                    changed = true;
                }

                if (CommitSection("people", BuildPeopleKey(metadata)))
                {
                    await _ui.PopulateCastAndDirectors(metadata);
                    changed = true;
                }

                if (HasEpisodes(metadata) && CommitSection("episodes", BuildEpisodesKey(metadata)))
                {
                    await _ui.LoadSeriesDataAsync(metadata);
                    HasCommittedEpisodes = true;
                    changed = true;
                }

                if (CommitSection("backdrop", BuildBackdropKey(metadata)))
                {
                    ApplyBackdrop(metadata);
                    changed = true;
                }

                if (CommitSection("attribution", BuildAttributionKey(metadata)))
                {
                    ApplyAttribution(metadata);
                    changed = true;
                }

                if (changed)
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

        private async Task ApplyInitialCommitAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            PrepareMetadataDefaults(metadata, item);

            ApplyIdentity(metadata, item);
            ApplyDetails(metadata);
            ApplyActions(metadata, item);
            await _ui.PopulateCastAndDirectors(metadata);
            ApplyBackdrop(metadata);
            ApplyAttribution(metadata);

            if (!string.IsNullOrEmpty(_ui.StreamUrl))
            {
                _ = _ui.UpdateTechnicalBadgesAsync(_ui.StreamUrl);
            }

            _ui.SyncLayout();
        }

        public bool IsIdentityAuthorityReady(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if ((DateTime.Now - _ui.NavigationStartTime).TotalMilliseconds > IdentityGateTimeoutMs)
            {
                return true;
            }

            bool hasPrimary = HasPrimarySource(metadata);
            if (!hasPrimary) return false;

            bool logoUrlExists = !string.IsNullOrWhiteSpace(metadata.LogoUrl);
            bool detailSweepFinished = metadata.CheckedFields.HasFlag(MetadataField.Logo);

            if (logoUrlExists && !_ui.IsLogoImageLoaded)
            {
                return false;
            }

            if (!logoUrlExists && !detailSweepFinished)
            {
                return false;
            }

            bool hasOverview = !string.IsNullOrWhiteSpace(metadata.Overview);
            if (!hasOverview && !metadata.CheckedFields.HasFlag(MetadataField.Overview))
            {
                return false;
            }

            return true;
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

        private void PrepareMetadataDefaults(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            if (metadata == null || item == null) return;
            if (string.IsNullOrWhiteSpace(metadata.PosterUrl)) metadata.PosterUrl = item.PosterUrl;
            if (string.IsNullOrWhiteSpace(metadata.BackdropUrl))
            {
                metadata.BackdropUrl = (item as Models.Stremio.StremioMediaStream)?.Meta?.Background ?? item.BackdropUrl;
            }
        }

        private void CaptureAll(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            _sectionKeys["identity"] = BuildIdentityKey(metadata, item);
            _sectionKeys["details"] = BuildDetailsKey(metadata);
            _sectionKeys["actions"] = BuildActionsKey(metadata);
            _sectionKeys["people"] = BuildPeopleKey(metadata);
            _sectionKeys["episodes"] = BuildEpisodesKey(metadata);
            _sectionKeys["backdrop"] = BuildBackdropKey(metadata);
            _sectionKeys["attribution"] = BuildAttributionKey(metadata);
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

                // [UX] For movies, we always want to show the sources panel immediately
                // so the user can see available quality options while reading metadata.
                _ui.OpenSourcesPanel(PanelChangeReason.SourceCache);
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

        private static string BuildIdentityKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata, IMediaStream item)
        {
            return string.Join("|", metadata.Title, metadata.SubTitle, metadata.OriginalTitle, metadata.LogoUrl, item.Title);
        }

        private static string BuildDetailsKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            return string.Join("|", metadata.Overview, metadata.Year, metadata.Genres, metadata.Runtime, metadata.Rating);
        }

        private static string BuildActionsKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            return string.Join("|", metadata.TrailerUrl, metadata.IsAvailableOnIptv, metadata.StreamUrl);
        }

        private static string BuildPeopleKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            string cast = metadata.Cast == null ? "" : string.Join(";", metadata.Cast.Take(10).Select(c => $"{c.Name}:{c.Character}:{c.ProfileUrl}"));
            string directors = metadata.Directors == null ? "" : string.Join(";", metadata.Directors.Take(5).Select(d => $"{d.Name}:{d.ProfileUrl}"));
            return $"{cast}|{directors}|{metadata.TmdbInfo?.Id}";
        }

        private static string BuildEpisodesKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            if (!HasEpisodes(metadata)) return "";
            return string.Join("|", metadata.Seasons.Select(s =>
                $"{s.SeasonNumber}:{s.Name}:{(s.Episodes == null ? 0 : s.Episodes.Count)}:{string.Join(",", (s.Episodes ?? new List<UnifiedEpisode>()).Take(4).Select(e => $"{e.Id}:{e.Title}:{e.StreamUrl}"))}"));
        }

        private static string BuildBackdropKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            string gallery = metadata.BackdropUrls == null ? "" : string.Join(";", metadata.BackdropUrls);
            return $"{metadata.BackdropUrl}|{metadata.PosterUrl}|{gallery}";
        }

        private static string BuildAttributionKey(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata metadata)
        {
            return $"{metadata.DataSource}|{metadata.MetadataSourceInfo}";
        }
    }
}
