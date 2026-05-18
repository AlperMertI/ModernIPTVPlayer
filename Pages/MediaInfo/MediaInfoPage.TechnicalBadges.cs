using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using ModernIPTVPlayer.Helpers;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        #region Technical Badge Generation & Dynamic Cross-Fade

        private void SetBadgeLoadingState(bool isLoading)
        {
            if (TechBadgesShimmer == null || TechBadgesContent == null || _compositor == null) return;

            if (isLoading)
            {
                if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                TechBadgesShimmer.Width = double.NaN;
                TechBadgesShimmer.Visibility = Visibility.Visible;
                
                var visShim = ElementCompositionPreview.GetElementVisual(TechBadgesShimmer);
                if (visShim != null) visShim.Opacity = 1f;

                TechBadgesContent.Visibility = Visibility.Collapsed;
                var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                if (visContent != null) visContent.Opacity = 0f;
            }
            else
            {
                // Loaded: Cross-fade to Badges or Collapse if empty
                bool spansSpace = HasVisibleBadges();

                if (spansSpace)
                {
                    // Fade In Badges
                    TechBadgesContent.Visibility = Visibility.Visible;
                    var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                    if (visContent != null)
                    {
                        visContent.Opacity = 0f;

                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(0f, 0f);
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;
                    }

                    if (MetadataRibbon != null) MetadataRibbon.Visibility = Visibility.Visible;
                }

                // Fade Out Shimmer
                var visShimmer = ElementCompositionPreview.GetElementVisual(TechBadgesShimmer);
                if (visShimmer != null)
                {
                    var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, 1f);
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromMilliseconds(300);

                    var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    visShimmer.StartAnimation("Opacity", fadeOut);
                    batch.Completed += (s, e) =>
                    {
                        if (TechBadgesShimmer != null)
                        {
                            TechBadgesShimmer.Visibility = Visibility.Collapsed;
                            TechBadgesShimmer.Width = double.NaN;
                        }
                        UpdateTechnicalSectionVisibility(spansSpace);
                    };
                    batch.End();
                }
                else
                {
                    TechBadgesShimmer.Visibility = Visibility.Collapsed;
                    UpdateTechnicalSectionVisibility(spansSpace);
                }
            }
        }

        private void AdjustTechBadgesShimmer()
        {
            if (TechBadgesShimmer == null || TechBadgesContent == null) return;
            
            var visibleBorders = TechBadgesContent.Children.OfType<Border>()
                                   .Where(c => c.Visibility == Visibility.Visible)
                                   .ToList();

            for (int i = 0; i < TechBadgesShimmer.Children.Count; i++)
            {
                var shim = TechBadgesShimmer.Children[i] as FrameworkElement;
                if (shim == null) continue;

                if (i < visibleBorders.Count)
                {
                    var border = visibleBorders[i];
                    shim.Visibility = Visibility.Visible;
                    shim.Width = 50; // Use stable fallback
                }
                else
                {
                    shim.Visibility = Visibility.Collapsed;
                }
            }
        }

        internal async Task UpdateTechnicalBadgesAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "UpdateTechnicalBadges CANCELLED", "Null URL");
                SetBadgeLoadingState(false);
                UpdateTechnicalSectionVisibility(false);
                return;
            }

            Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "UpdateTechnicalBadges START", url);
            
            // Ensure shimmer is visible while probing
            SetBadgeLoadingState(true);
            UpdateTechnicalSectionVisibility(false);

            int currentVersion = Volatile.Read(ref _loadingVersion);

            // Cancel previous probe
            try
            {
                _probeCts?.Cancel();
                _probeCts?.Dispose(); 
            }
            catch { } 

            _probeCts = new CancellationTokenSource();
            var token = _probeCts.Token;

            try
            {
                // 0. CHECK IPTV METADATA FIRST - Avoid Probing if possible
                string metadataRes = null;
                string metadataCodec = null;
                long metadataBitrate = 0;
                bool? metadataHdr = null;

                if (_selectedEpisode != null)
                {
                    metadataRes = _selectedEpisode.Resolution;
                    metadataCodec = _selectedEpisode.VideoCodec;
                    metadataBitrate = _selectedEpisode.Bitrate;
                    metadataHdr = _selectedEpisode.IsHdr;
                }
                else if (_unifiedMetadata != null)
                {
                    metadataRes = _unifiedMetadata.Resolution;
                    metadataCodec = _unifiedMetadata.VideoCodec;
                    metadataBitrate = _unifiedMetadata.Bitrate;
                    metadataHdr = _unifiedMetadata.IsHdr;
                }

                if (!string.IsNullOrEmpty(metadataRes) || !string.IsNullOrEmpty(metadataCodec))
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "IPTV METADATA: Skipping probe, using provided info", url);
                    var probeData = new Services.ProbeData
                    {
                        Resolution = metadataRes,
                        Codec = metadataCodec,
                        Bitrate = metadataBitrate,
                        IsHdr = metadataHdr ?? false
                    };
                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData, currentVersion);
                        SetBadgeLoadingState(false);
                    });
                    return;
                }

                // UI Reset for new probe
                Badge4K.Visibility = Visibility.Collapsed;
                BadgeRes.Visibility = Visibility.Collapsed;
                BadgeHDR.Visibility = Visibility.Collapsed;
                BadgeSDR.Visibility = Visibility.Collapsed;
                BadgeCodecContainer.Visibility = Visibility.Collapsed;

                // 1. Check ID-Based Binary Cache
                await Services.ProbeCacheService.Instance.EnsureLoadedAsync();
                if (Services.ProbeCacheService.Instance.Get(_item.Id) is Services.ProbeData cached)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.MediaInfo, "Badges Cache Hit", url);

                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        TechBadgesContent.Opacity = 0;
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(cached, currentVersion);

                        // Quick Fade In
                        var visContent = ElementCompositionPreview.GetElementVisual(TechBadgesContent);
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(0f, 0f);
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(50);
                        visContent.StartAnimation("Opacity", fadeIn);
                        TechBadgesContent.Opacity = 1;

                        SetBadgeLoadingState(false);
                    });
                    return;
                }

                // 2. Show Shimmer
                SetBadgeLoadingState(true);

                // 3. SMART PROBE: Check if existing player is already opening this URL
                Services.ProbeResult probeResult;

                if (MediaInfoPlayer != null && _prebufferUrl == url)
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "SMART PROBE: Reusing prebuffer player", url);
                    // Wait for the active player to get metadata
                    probeResult = await Services.StreamProberService.ExtractProbeDataAsync(MediaInfoPlayer, token);
                }
                else
                {
                    Services.CacheLogger.Info(Services.CacheLogger.Category.MediaInfo, "DEDICATED PROBE: Starting prober service", url);
                    probeResult = await Services.StreamProberService.Instance.ProbeAsync(_item.Id, url, progress: null, token);
                }

                if (token.IsCancellationRequested) return;

                if (probeResult.Success)
                {
                    // Manual cache update for SMART PROBE if needed
                    if (MediaInfoPlayer != null && _prebufferUrl == url)
                    {
                        Services.ProbeCacheService.Instance.Update(_item.Id, new Services.ProbeData 
                        { 
                            Resolution = probeResult.Resolution, 
                            Fps = probeResult.Fps, 
                            Codec = probeResult.Codec, 
                            Bitrate = probeResult.Bitrate, 
                            IsHdr = probeResult.IsHdr 
                        });
                    }

                    var probeData = new Services.ProbeData
                    {
                        Resolution = probeResult.Resolution,
                        Fps = probeResult.Fps,
                        Codec = probeResult.Codec,
                        Bitrate = probeResult.Bitrate,
                        IsHdr = probeResult.IsHdr
                    };

                    TryEnqueueForLoadSession(currentVersion, () =>
                    {
                        TechBadgesContent.Visibility = Visibility.Visible;
                        ApplyMetadataToUi(probeData, currentVersion);
                        SetBadgeLoadingState(false);
                    });
                }
                else
                {
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.MediaInfo, "Probe Failed", url);
                    TryEnqueueForLoadSession(currentVersion, () => SetBadgeLoadingState(false));
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.MediaInfo, "Probe Error", ex.Message);
                TryEnqueueForLoadSession(currentVersion, () => SetBadgeLoadingState(false));
            }
        }

        private void ApplyMetadataToUi(Services.ProbeData result, int currentVersion)
        {
            if (result == null) return;

            // 1. Prepare display values
            bool is4K = !string.IsNullOrEmpty(result.Resolution) && (result.Resolution.Contains("3840") || result.Resolution.Contains("4096") || result.Resolution.ToUpperInvariant().Contains("4K"));
            
            string displayRes = result.Resolution;
            if (!string.IsNullOrEmpty(displayRes) && displayRes.Contains("x"))
            {
                var h = displayRes.Split('x').LastOrDefault();
                if (h != null) displayRes = h + "P";
            }
            
            string finalResBadge = is4K ? "4K" : (string.IsNullOrWhiteSpace(displayRes) || displayRes == "Unknown" || displayRes == "N/A" ? null : displayRes.ToUpperInvariant());

            // 2. Sync to Source List (StremioStreamViewModel)
            if (_addonResults != null)
            {
                var activeStream = _addonResults.SelectMany(a => a.Streams ?? new System.Collections.Generic.List<StremioStreamViewModel>()).FirstOrDefault(s => s.IsActive);
                if (activeStream != null)
                {
                    if (!string.IsNullOrEmpty(finalResBadge)) activeStream.Quality = finalResBadge;
                    activeStream.IsHdr = result.IsHdr;
                    activeStream.Codec = result.Codec;
                }
            }

            // 3. Update Info Panel Badges
            if (Badge4K != null) Badge4K.Visibility = is4K ? Visibility.Visible : Visibility.Collapsed;

            if (!is4K && !string.IsNullOrEmpty(finalResBadge))
            {
                if (BadgeResText != null) BadgeResText.Text = finalResBadge;
                if (BadgeRes != null) BadgeRes.Visibility = Visibility.Visible;
            }
            else if (BadgeRes != null)
            {
                BadgeRes.Visibility = Visibility.Collapsed;
            }

            // HDR / SDR
            if (BadgeHDR != null) BadgeHDR.Visibility = result.IsHdr ? Visibility.Visible : Visibility.Collapsed;
            if (BadgeSDR != null) BadgeSDR.Visibility = !result.IsHdr ? Visibility.Visible : Visibility.Collapsed;

            // Codec
            if (!string.IsNullOrWhiteSpace(result.Codec) && 
                result.Codec != "-" && result.Codec != "Unknown" && result.Codec != "Error" && result.Codec != "N/A" &&
                result.Codec.Trim().Length > 0)
            {
                if (BadgeCodec != null) BadgeCodec.Text = result.Codec;
                if (BadgeCodecContainer != null) BadgeCodecContainer.Visibility = Visibility.Visible;
            }
            else if (BadgeCodecContainer != null)
            {
                BadgeCodecContainer.Visibility = Visibility.Collapsed;
            }

            // Bitrate
            if (result.Bitrate > 0)
            {
                double mbps = result.Bitrate / 1000000.0;
                string formatted = mbps >= 1.0 ? $"{mbps:F1} Mbps" : $"{result.Bitrate / 1000} kbps";
                if (BadgeBitrateText != null) BadgeBitrateText.Text = formatted;
                if (BadgeBitrate != null) BadgeBitrate.Visibility = Visibility.Visible;
            }
            else if (BadgeBitrate != null)
            {
                BadgeBitrate.Visibility = Visibility.Collapsed;
            }

            // Age Rating & Country (from UnifiedMetadata)
            // Race condition guard: If user navigated away or changed items, stop here.
            if (!IsCurrentLoadSession(currentVersion)) return;

            if (_unifiedMetadata != null)
            {
                if (BadgeAge != null)
                {
                    bool hasAge = !string.IsNullOrEmpty(_unifiedMetadata.AgeRating);
                    BadgeAge.Visibility = hasAge ? Visibility.Visible : Visibility.Collapsed;
                    if (hasAge && BadgeAgeText != null) BadgeAgeText.Text = _unifiedMetadata.AgeRating;
                }
                if (BadgeCountry != null)
                {
                    bool hasCountry = !string.IsNullOrEmpty(_unifiedMetadata.Country);
                    BadgeCountry.Visibility = hasCountry ? Visibility.Visible : Visibility.Collapsed;
                    if (hasCountry && BadgeCountryText != null) BadgeCountryText.Text = _unifiedMetadata.Country;
                }
            }

            UpdateTechnicalSectionVisibility(HasVisibleBadges());
        }

        private bool HasVisibleBadges() =>
            (BadgeAge != null && BadgeAge.Visibility == Visibility.Visible) ||
            (BadgeCountry != null && BadgeCountry.Visibility == Visibility.Visible) ||
            Badge4K.Visibility == Visibility.Visible ||
            BadgeRes.Visibility == Visibility.Visible ||
            BadgeHDR.Visibility == Visibility.Visible ||
            BadgeSDR.Visibility == Visibility.Visible ||
            BadgeCodecContainer.Visibility == Visibility.Visible ||
            BadgeBitrate.Visibility == Visibility.Visible;

        #endregion
    }
}
