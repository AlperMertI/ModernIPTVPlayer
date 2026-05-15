using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Partial class for MediaInfoPage focusing on Hero Background management.
    /// Centralizes crossfading, seeding, and visual stability for the cinematic backdrop.
    /// </summary>
    public partial class MediaInfoPage
    {
        private enum AmbienceState { None, Provisional, Stable }
        private class BackdropEntry
        {
            public string Url { get; set; }
            public string Signature { get; set; }
            public int Area { get; set; }
        }

        private string? _currentHeroUrl;
        private bool _isBackgroundInitialized = false;

        private bool _isFirstImageApplied = false;
        private bool _isHeroTransitionInProgress = false;
        private System.Threading.CancellationTokenSource? _heroCts;

        private DispatcherTimer _slideshowTimer;
        private string _slideshowId;
        private List<string> _backdropUrls = new List<string>();
        private int _currentBackdropIndex = 0;
        private HashSet<string> _backdropKeys = new HashSet<string>();
        private string _lastVisualSignature;
        private List<BackdropEntry> _validatedBackdrops = new List<BackdropEntry>();

        private Image GetActiveHeroImage()
        {
            if (HeroImage == null || HeroImage2 == null) return HeroImage;
            return (HeroImage.Opacity >= HeroImage2.Opacity) ? HeroImage : HeroImage2;
        }

        private Image GetInactiveHeroImage()
        {
            if (HeroImage == null || HeroImage2 == null) return HeroImage2;
            return (HeroImage.Opacity < HeroImage2.Opacity) ? HeroImage : HeroImage2;
        }

        private bool IsHighQuality(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string lower = url.ToLowerInvariant();
            return !lower.Contains("poster") && !lower.Contains("/small/") && !lower.Contains("_sx") && !lower.Contains("_sy") && !lower.Contains("w600_and_h900");
        }


        // Ambience Engine State
        private readonly System.Threading.SemaphoreSlim _ambienceLock = new System.Threading.SemaphoreSlim(1, 1);
        private AmbienceState _ambienceState = AmbienceState.None;
        private string _lastAmbienceUrl = null;
        private string _lastAmbienceSignature = null;
        private int _ambienceNavigationEpoch;
        private string _primaryColorHex = "#FF00BFA5"; // Default teal
        private Color _lastApplyPrimary;
        private Color _lastApplyArea;
        private Color _lastAreaColor;




        private void InitializeBackground()
        {
            if (_isBackgroundInitialized) return;
            
            // Ensure target buffer (HeroImage2) starts at 0
            HeroImage2.Opacity = 0;
            var v2 = ElementCompositionPreview.GetElementVisual(HeroImage2);
            v2.Opacity = 0f;
            
            _isBackgroundInitialized = true;
        }

        private void ResetBackground()
        {
            _heroCts?.Cancel();
            _heroCts?.Dispose();
            _heroCts = null;

            _backdropUrls?.Clear();
            _validatedBackdrops?.Clear();
            _backdropKeys?.Clear();

            StopBackgroundSlideshow();

            _ambienceNavigationEpoch++;

            if (HeroImage != null)
            {
                HeroImage.Source = null;
                var v1 = ElementCompositionPreview.GetElementVisual(HeroImage);
                v1.Opacity = 0;
                HeroImage.Opacity = 0;
            }
            if (HeroImage2 != null)
            {
                HeroImage2.Source = null;
                var v2 = ElementCompositionPreview.GetElementVisual(HeroImage2);
                v2.Opacity = 0;
                HeroImage2.Opacity = 0;
            }

            _currentHeroUrl = null;
            _isFirstImageApplied = false;

            if (HeroShimmer != null)
            {
                HeroShimmer.Visibility = Visibility.Visible;
                HeroShimmer.Opacity = 0.15;
            }

        }


        private void ApplyHeroBackgroundAction(ImageSource? source, string reason)
        {
            if (source == null) return;
            InitializeBackground();

            Image outgoing = GetActiveHeroImage();
            Image incoming = GetInactiveHeroImage();


            // If we are already showing this exact object on the active image, just ensure it's visible.
            if (outgoing.Source == source && outgoing.Opacity > 0.9)
            {
                _ = ExtractAndApplyAmbienceAsync(outgoing, $"seed-sync {reason}");
                return;
            }

            PerformHeroCrossfade(source, reason, null);
        }

        private void ApplyHeroBackgroundAction(string? imageUrl, string reason)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return;
            InitializeBackground();

            // Deduplication
            if (IsAlreadyShowing(imageUrl))
            {
                _ = ExtractAndApplyAmbienceAsync(GetActiveHeroImage(), $"seed-skipped {reason}");
                return;
            }


            PerformHeroCrossfade(ImageHelper.GetImage(imageUrl), reason, imageUrl);
        }

        private void PerformHeroCrossfade(ImageSource source, string reason, string? url)
        {
            try
            {
                // [Senior] Transition Engine Orchestration:
                // We use a dual-layer crossfade. 'incoming' is the target hidden layer.
                Image incoming = GetInactiveHeroImage();
                Image outgoing = GetActiveHeroImage();

                var visualIn = ElementCompositionPreview.GetElementVisual(incoming);
                var visualOut = ElementCompositionPreview.GetElementVisual(outgoing);


                // 1. Initial Show Logic (Poster/Handoff)
                if (!_isFirstImageApplied)
                {
                    _isFirstImageApplied = true;
                    _currentHeroUrl = url;
                    
                    bool isReady = ImageHelper.IsCached(url) || (source is not BitmapImage bi) || (bi.PixelWidth > 0);
                    incoming.Source = source;
                    
                    if (isReady)
                    {
                        incoming.Opacity = 1;
                        visualIn.Opacity = 1f;

                        _ = ExtractAndApplyAmbienceAsync(incoming, $"hero-first {reason}");
                        if (HeroShimmer != null) HeroShimmer.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        // Hidden until ImageOpened fires
                        incoming.Opacity = 0;
                        visualIn.Opacity = 0f;
                        System.Diagnostics.Debug.WriteLine($"[HERO] First image pending load: {url}");
                    }
                    StartKenBurnsEffect(incoming);
                    return;
                }

                // 2. High-Performance Hijack Logic:
                // If we are currently blank (still loading the first image), 
                // we want the NEW (likely higher quality) source to take over immediately.
                if (outgoing.Opacity < 0.1)
                {
                    System.Diagnostics.Debug.WriteLine($"[HERO] Hijacking blank state: {reason} | url={url}");
                    _currentHeroUrl = url;
                    _ = PerformHeroCrossfadeAsync(url, reason.Contains("prime") ? 0.4 : 1.2);
                    return;
                }

                // 3. Standard Crossfade Engine
                _ = PerformHeroCrossfadeAsync(url, reason.Contains("prime") ? 0.4 : 1.8);

                
                // Ensure Ken Burns is running on the target layer if it's already showing
                StartKenBurnsEffect(incoming);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HERO-ERROR] Crossfade failed: {ex.Message}");
            }
        }


        private string NormalizeUrl(string url) => url?.Replace("https://", "http://")?.TrimEnd('/')?.ToLowerInvariant();

        private bool IsAlreadyShowing(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            string normTarget = NormalizeUrl(url);

            // [Senior] Physical Visibility Check:
            // Only skip if one of the slots is actually VISIBLE (> 80% opacity) and showing the target URL.
            // Relying on logical _currentHeroUrl alone is dangerous as it might be set but the image 
            // failed to load or the animation was never triggered.
            if (HeroImage != null && HeroImage.Opacity > 0.8 && NormalizeUrl(GetImageSourceUrl(HeroImage)) == normTarget) return true;
            if (HeroImage2 != null && HeroImage2.Opacity > 0.8 && NormalizeUrl(GetImageSourceUrl(HeroImage2)) == normTarget) return true;

            return false;
        }


        private void EnsureHeroVisuals()
        {
            if (_compositor == null) return;

            // Synchronize WinUI UIElement opacity with Composition visuals
            // This is critical for Project Zero's dual-layer crossfade system.
            var v1 = ElementCompositionPreview.GetElementVisual(HeroImage);
            var v2 = ElementCompositionPreview.GetElementVisual(HeroImage2);

            v1.Opacity = (float)HeroImage.Opacity;
            v2.Opacity = (float)HeroImage2.Opacity;

            // Center points for scale animations (Ken Burns)
            v1.CenterPoint = new Vector3((float)HeroImage.ActualWidth / 2, (float)HeroImage.ActualHeight / 2, 0);
            v2.CenterPoint = new Vector3((float)HeroImage2.ActualWidth / 2, (float)HeroImage2.ActualHeight / 2, 0);
        }

        private static string GetImageSourceUrl(Image image)
        {
            if (image == null || image.Source == null) return null;
            if (image.Source is BitmapImage bi) return bi.UriSource?.ToString();
            return null;
        }

        private async Task PerformUpgradeCrossfadeAsync(string url)
        {
            await PerformHeroCrossfadeAsync(url, 1.8);
        }

        private async Task PerformHeroCrossfadeAsync(string imageUrl, double durationSeconds = 1.8, Func<Image, Task<bool>> onOpenedAsync = null)
        {
            if (string.IsNullOrEmpty(imageUrl) || HeroImage == null || HeroImage2 == null) return;

            // [Senior] Cancellation Management:
            // If a new transition is requested (e.g. an Immediate Upgrade), we CANCEL the previous one
            // and proceed immediately. The previous task will abort at its next check-point.
            _heroCts?.Cancel();
            _heroCts?.Dispose();
            _heroCts = new System.Threading.CancellationTokenSource();
            var ct = _heroCts.Token;

            // Toggle Logic: Load into the INACTIVE image
            Image incoming = GetInactiveHeroImage();
            Image outgoing = GetActiveHeroImage();

            if (IsAlreadyShowing(imageUrl)) return;

            try
            {
                _isHeroTransitionInProgress = true;
                _currentHeroUrl = imageUrl;

                // Ensure compositor is ready
                if (_compositor == null) EnsureHeroVisuals();


                var visualIncoming = ElementCompositionPreview.GetElementVisual(incoming);
                var visualOutgoing = ElementCompositionPreview.GetElementVisual(outgoing);

                // [CRITICAL] Pre-hide the incoming layer completely before setting source
                visualIncoming.Opacity = 0;

                RoutedEventHandler openedHandler = null;
                ExceptionRoutedEventHandler failedHandler = null;

                bool isResolved = false;

                Action cleanup = () =>
                {
                    incoming.ImageOpened -= openedHandler;
                    incoming.ImageFailed -= failedHandler;
                };

                openedHandler = async (s, e) =>
                {
                    if (isResolved || ct.IsCancellationRequested) return;
                    isResolved = true;

                    // Execute custom logic while image is loaded but still invisible
                    bool shouldProceed = true;
                    if (onOpenedAsync != null) shouldProceed = await onOpenedAsync(incoming);
                    
                    cleanup();
                    
                    if (ct.IsCancellationRequested || !shouldProceed) 
                    {
                        _isHeroTransitionInProgress = false;
                        return;
                    }

                    // Hide shimmer as soon as we have something to show
                    if (HeroShimmer != null) HeroShimmer.Visibility = Visibility.Collapsed;



                    // Ensure it is still hidden before we start the animation
                    visualIncoming.Opacity = 0;

                    // Start Ken Burns on Incoming
                    StartKenBurnsEffect(incoming);

                    // Crossfade using actual current values to prevent "force-jump" flickers
                    var startIn = (float)incoming.Opacity;
                    var startOut = (float)outgoing.Opacity;

                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(0f, startIn);
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromSeconds(durationSeconds);

                    var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                    fadeOut.InsertKeyFrame(0f, startOut);
                    fadeOut.InsertKeyFrame(1f, 0f);
                    fadeOut.Duration = TimeSpan.FromSeconds(durationSeconds);


                    visualIncoming.StartAnimation("Opacity", fadeIn);
                    visualOutgoing.StartAnimation("Opacity", fadeOut);

                    System.Diagnostics.Debug.WriteLine(
                        $"[INFO-UI][CROSSFADE] start incoming={GetImageSourceUrl(incoming) ?? "<null>"} outgoing={GetImageSourceUrl(outgoing) ?? "<null>"} inOpacity={incoming.Opacity:F2} outOpacity={outgoing.Opacity:F2} duration={durationSeconds:F2}s");



                    // Finalize after transition
                    _ = Task.Delay(TimeSpan.FromSeconds(durationSeconds) + TimeSpan.FromMilliseconds(80), ct).ContinueWith(t =>
                    {
                        if (t.IsCanceled) return;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            _isHeroTransitionInProgress = false;
                            
                            // [Senior] Synchronize WinUI properties with finished Composition animations.
                            // This is critical for GetActiveHeroImage() to correctly identify slots.
                            incoming.Opacity = 1;
                            outgoing.Opacity = 0;
                            
                            // Clean up outgoing source to free memory
                            outgoing.Source = null;
                        });
                    }, TaskScheduler.Default);

                };

                failedHandler = (s, e) =>
                {
                    if (isResolved || ct.IsCancellationRequested) return;
                    isResolved = true;
                    cleanup();
                    _isHeroTransitionInProgress = false;
                    System.Diagnostics.Debug.WriteLine($"[INFO-UI] Crossfade Failed for {imageUrl}: {e.ErrorMessage}");
                };

                incoming.ImageOpened += openedHandler;
                incoming.ImageFailed += failedHandler;
                incoming.Source = new BitmapImage(new Uri(imageUrl));

                // Guard against hanging
                _ = Task.Delay(10000, ct).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    if (!isResolved)
                    {
                        isResolved = true;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            cleanup();
                            _isHeroTransitionInProgress = false;
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                _isHeroTransitionInProgress = false;
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] Crossfade Error: {ex.Message}");
            }
        }

        private void OnHeroSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Image img)
            {
                var v = ElementCompositionPreview.GetElementVisual(img);
                v.CenterPoint = new Vector3((float)img.ActualWidth / 2f, (float)img.ActualHeight / 2f, 0);
            }
        }

        private void AddBackdropToSlideshow(string url)
        {
            if (string.IsNullOrEmpty(url) || _backdropUrls == null) return;
            if (ImageHelper.IsPlaceholder(url)) return;

            // [Senior] Aggressive Filtering:
            // Skip images that are explicitly marked as posters or small thumbnails.
            // These should never be part of the cinematic background slideshow.
            string lowerUrl = url.ToLowerInvariant();
            if (lowerUrl.Contains("poster") || lowerUrl.Contains("/small/") || lowerUrl.Contains("w600_and_h900") || lowerUrl.Contains("_sx") || lowerUrl.Contains("_sy")) return;

            string key = GetNormalizedImageKey(url);

            if (_backdropKeys.Contains(key)) return;

            // [Senior] Skip if it's the CURRENT poster/hero image.
            // We don't want the low-quality poster to reappear in the cinematic backdrop rotation.
            if (!string.IsNullOrEmpty(_currentHeroUrl) && GetNormalizedImageKey(_currentHeroUrl) == key) return;

            _backdropKeys.Add(key);
            _backdropUrls.Add(url);


            // If this is the second image and no timer is running, start it
            if (_backdropUrls.Count == 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
            {
                InitializeSlideshowTimer();
            }
        }

        private void StartBackgroundSlideshow(List<string> images)
        {
            if (images == null || images.Count == 0 || HeroImage == null) return;

            // Deduplicate using a stable ID that won't change after metadata enrichments (Title based)
            string currentId = $"{_item?.Title}";
            if (!string.IsNullOrEmpty(_item?.IMDbId)) currentId += $"_{_item.IMDbId.Replace("imdb_id:", "")}";

            // If the slideshow ID matches, add new images incrementally without resetting.
            if (_slideshowId == currentId)
            {
                System.Diagnostics.Debug.WriteLine($"[INFO-UI] Item match ({currentId}). Adding {images.Count} images incrementally.");
                foreach (var img in images)
                {
                    AddBackdropToSlideshow(img);
                }

                if (_backdropUrls != null && _backdropUrls.Count > 1 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
                {
                    InitializeSlideshowTimer();
                }
                return;
            }

            _slideshowId = currentId;

            // Stop existing timer
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }

            // [NEW] Completely reset for a fundamentally new item
            _backdropKeys.Clear();
            _validatedBackdrops.Clear();
            _backdropUrls = new List<string>();
            _currentBackdropIndex = 0;

            System.Diagnostics.Debug.WriteLine($"[INFO-UI] New Slideshow ID: {currentId}. Offloading {images.Count} images.");

            foreach (var img in images)
            {
                AddBackdropToSlideshow(img);
            }

            // [Senior] Immediate Upgrade Logic
            // If we are currently showing a "provisional" image (poster) and we just got 
            // a high-quality backdrop list, don't wait for the 8s rotation timer.
            if (_isFirstImageApplied && !IsHighQuality(_currentHeroUrl) && _backdropUrls.Count > 0)
            {
                string firstBackdrop = _backdropUrls[0];
                if (NormalizeUrl(firstBackdrop) != NormalizeUrl(_currentHeroUrl))
                {
                    System.Diagnostics.Debug.WriteLine($"[INFO-UI] Immediate Upgrade: Swapping poster for first backdrop: {firstBackdrop}");
                    _ = PerformHeroCrossfadeAsync(firstBackdrop, 1.8);
                }
            }

            // Ensure visuals are ready
            EnsureHeroVisuals();
            
            // If we have backdrops, ensure the timer is initialized (if not already done by AddBackdrop)
            if (_backdropUrls.Count >= 2 && (_slideshowTimer == null || !_slideshowTimer.IsEnabled))
            {
                InitializeSlideshowTimer();
            }

        }

        private void InitializeSlideshowTimer()
        {
            if (_backdropUrls == null || _backdropUrls.Count <= 1) return;
            if (_slideshowTimer != null) _slideshowTimer.Stop();

            _slideshowTimer = new DispatcherTimer();
            _slideshowTimer.Interval = TimeSpan.FromSeconds(8);
            _slideshowTimer.Tick += async (s, e) =>
            {
                if (HeroImage == null || HeroImage2 == null || _backdropUrls == null || _backdropUrls.Count <= 1 || _isHeroTransitionInProgress)
                {
                    return;
                }

                _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
                string nextImgUrl = _backdropUrls[_currentBackdropIndex];

                await PerformHeroCrossfadeAsync(nextImgUrl, 2.0, async (incoming) =>
                {
                    // [Senior] Perceptual Deduplication Loop
                    string signature = await CalculateVisualSignatureAsync(incoming);
                    if (signature == null) return true;

                    // Search for this signature in previously validated ones
                    var existing = _validatedBackdrops.FirstOrDefault(v => IsSignatureSimilar(signature, v.Signature));
                    
                    if (existing != null)
                    {
                        if (existing.Url == nextImgUrl)

                        {
                            // [Senior] This is the SAME asset we already validated in a previous loop iteration.
                            // It's just the loop coming around naturally. PROCEED.
                            _lastVisualSignature = signature;
                            return true;
                        }
                        else
                        {
                            // [Senior] DIFFERENT URL but SAME image (e.g. MetaHub vs TMDB).
                            // This is a redundant duplicate. PURGE it from the rotation.
                            System.Diagnostics.Debug.WriteLine($"[INFO-UI] Purging perceptual duplicate ({existing.Url}): {nextImgUrl}");
                            _backdropUrls.RemoveAt(_currentBackdropIndex);
                            _backdropKeys.Remove(GetNormalizedImageKey(nextImgUrl));
                            _currentBackdropIndex = (_currentBackdropIndex - 1 + _backdropUrls.Count) % _backdropUrls.Count;
                            return false; // Abort this transition
                        }
                    }

                    // Not a duplicate - record it and apply ambience
                    _validatedBackdrops.Add(new BackdropEntry { Url = nextImgUrl, Signature = signature });
                    _lastVisualSignature = signature;
                    
                    _ = ExtractAndApplyAmbienceAsync(incoming, "slideshow validated");
                    return true; 
                });


            };
            _slideshowTimer.Start();
        }
        private string GetNormalizedImageKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            try
            {
                string lowerUrl = url.ToLowerInvariant();

                // 1. Handle MetaHub (MetaHub uses IMDb ID in path)
                if (lowerUrl.Contains("metahub.space"))
                {
                    // Pattern: .../tt1234567/...
                    var match = System.Text.RegularExpressions.Regex.Match(lowerUrl, @"(tt\d+)");
                    if (match.Success) return $"metahub_{match.Value}";
                }

                // 2. Handle TMDB (Normalize resolutions and focus on the hash)
                if (lowerUrl.Contains("tmdb.org"))
                {
                    int lastSlash = lowerUrl.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        string filename = lowerUrl.Substring(lastSlash + 1);
                        int dot = filename.LastIndexOf('.');
                        if (dot >= 0) filename = filename.Substring(0, dot);
                        return filename;
                    }
                }

                // 3. Fallback: Clean common noise
                var key = url.Replace("https://", "").Replace("http://", "");
                int queryIdx = key.IndexOf('?');
                if (queryIdx > 0) key = key.Substring(0, queryIdx);
                return key.TrimEnd('/');
            }
            catch { return url; }
        }

        private async Task<string> CalculateVisualSignatureAsync(Image img)
        {
            if (img == null || img.Source is not BitmapImage bi) return null;
            
            // [Senior] Perceptual Signature:
            // Compare dominant colors and aspect ratio rather than just URL.
            // This detects identical images served from different CDNs (MetaHub vs TMDB).
            var colors = await ImageHelper.ExtractColorsAsync(bi);
            float ratio = (bi.PixelWidth > 0 && bi.PixelHeight > 0) ? (float)bi.PixelWidth / bi.PixelHeight : 1.0f;
            
            return $"{colors.Primary.R:X2}{colors.Primary.G:X2}{colors.Primary.B:X2}_" +
                   $"{colors.Secondary.R:X2}{colors.Secondary.G:X2}{colors.Secondary.B:X2}_" +
                   $"{ratio:F2}";
        }

        private bool IsSignatureSimilar(string sig1, string sig2)
        {
            if (string.IsNullOrEmpty(sig1) || string.IsNullOrEmpty(sig2)) return false;
            if (sig1 == sig2) return true;
            
            // Allow minor floating point differences in ratio
            var parts1 = sig1.Split('_');
            var parts2 = sig2.Split('_');
            
            if (parts1.Length < 3 || parts2.Length < 3) return false;
            
            // Colors must match exactly
            if (parts1[0] != parts2[0] || parts1[1] != parts2[1]) return false;
            
            return true;
        }
        private void StopBackgroundSlideshow()
        {
            if (_slideshowTimer != null)
            {
                _slideshowTimer.Stop();
                _slideshowTimer = null;
            }
            _slideshowId = null;
        }

        private void AddBackdropToSlideshowInternal(string url) { if (this.DispatcherQueue.HasThreadAccess) AddBackdropToSlideshow(url); else this.DispatcherQueue.TryEnqueue(() => AddBackdropToSlideshow(url)); }
        private void StartBackgroundSlideshowInternal(List<string> urls) { if (this.DispatcherQueue.HasThreadAccess) StartBackgroundSlideshow(urls); else this.DispatcherQueue.TryEnqueue(() => StartBackgroundSlideshow(urls)); }
        private void ApplyHeroSeedImageInternal(string url, string reason) { if (this.DispatcherQueue.HasThreadAccess) ApplyHeroSeedImage(url, reason); else this.DispatcherQueue.TryEnqueue(() => ApplyHeroSeedImage(url, reason)); }

        private string GetCurrentBackdrop()
        {
            Image active = GetActiveHeroImage();
            return (active?.Source as BitmapImage)?.UriSource?.ToString();
        }

        private async Task ExtractAndApplyAmbienceAsync(Image sourceImage = null, string sourceLabel = null)
        {
            var tid = Environment.CurrentManagedThreadId;
            var currentUrl = (sourceImage?.Source as BitmapImage)?.UriSource?.ToString();
            bool isValidated = sourceLabel != null && sourceLabel.Contains("validated");

            int startEpoch = System.Threading.Volatile.Read(ref _ambienceNavigationEpoch);

            // Skip if the source hasn't changed or it's a downgrade
            string currentSignature = BuildAmbienceSignature(currentUrl, sourceLabel);
            if (!string.IsNullOrEmpty(currentSignature) && _lastAmbienceSignature == currentSignature) return;

            await _ambienceLock.WaitAsync();
            try
            {
                if (startEpoch != System.Threading.Volatile.Read(ref _ambienceNavigationEpoch)) return;
                if (_lastAmbienceSignature == currentSignature) return;

                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INFO-AMBIENCE] Extraction START on TID: {tid} | Source: {sourceLabel}");

                if (sourceImage != null && sourceImage.Source is BitmapImage rtb)
                {
                    if (startEpoch != System.Threading.Volatile.Read(ref _ambienceNavigationEpoch)) return;

                    var colors = await ImageHelper.ExtractColorsAsync(rtb);

                    if (rtb.PixelWidth == 0 || startEpoch != System.Threading.Volatile.Read(ref _ambienceNavigationEpoch)) return;

                    var areaColor = ImageHelper.GenerateAreaBackground(colors.Primary);

                    // Update State before Apply to avoid re-triggering while animating
                    _lastAmbienceUrl = currentUrl;
                    _lastAmbienceSignature = currentSignature;
                    if (isValidated) _ambienceState = AmbienceState.Stable;
                    else if (_ambienceState == AmbienceState.None) _ambienceState = AmbienceState.Provisional;

                    ApplyPremiumAmbience(colors.Primary, areaColor, sourceLabel);
                }
            }
            catch { }
            finally
            {
                _ambienceLock.Release();
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [INFO-AMBIENCE] Extraction EXIT on TID: {tid}");
            }
        }

        private static string BuildAmbienceSignature(string url, string sourceLabel)
        {
            return $"{url ?? "<null>"}|{NormalizeAmbienceSourceLabel(sourceLabel)}";
        }

        private static string NormalizeAmbienceSourceLabel(string sourceLabel)
        {
            if (string.IsNullOrEmpty(sourceLabel)) return "default";
            if (sourceLabel.Contains("validated")) return "validated";
            if (sourceLabel.Contains("image opened")) return "opened";
            if (sourceLabel.Contains("prime")) return "prime";
            return sourceLabel;
        }

        private void ApplyPremiumAmbience(Color primary, Color areaBackground, string sourceLabel = null)
        {
            _lastAreaColor = areaBackground;
            _lastApplyPrimary = primary;
            _lastApplyArea = areaBackground;
            _primaryColorHex = $"#{primary.A:X2}{primary.R:X2}{primary.G:X2}{primary.B:X2}";
            bool isProvisionalSource = !string.IsNullOrWhiteSpace(sourceLabel) &&
                sourceLabel.StartsWith("provisional", StringComparison.OrdinalIgnoreCase);

            System.Diagnostics.Debug.WriteLine(
                $"[INFO-AMBIENCE] Apply start | primary={primary.R},{primary.G},{primary.B} | area={areaBackground.R},{areaBackground.G},{areaBackground.B}");

            // Final safety check to ensure _primaryColorHex is a valid hex color string
            if (string.IsNullOrEmpty(_primaryColorHex) || _primaryColorHex == "#00000000" || _primaryColorHex.Contains("Unknown"))
            {
                _primaryColorHex = "#FFFFFFFF"; // Fallback to safe white (with full opacity)
            }

            try
            {
                // 1. Prepare Base Tints for UI elements
                var btnTint = Color.FromArgb(50, primary.R, primary.G, primary.B);
                var mixedPanelTint = Color.FromArgb(180, (byte)(primary.R * 0.2), (byte)(primary.G * 0.2), (byte)(primary.B * 0.2));
                var playButtonTint = Color.FromArgb(90, primary.R, primary.G, primary.B);
                var playBorderTint = Color.FromArgb(140, primary.R, primary.G, primary.B);

                // 2. Analyze Background Area
                double areaL = (0.2126 * areaBackground.R + 0.7152 * areaBackground.G + 0.0722 * areaBackground.B) / 255.0;

                // Calculate vibrancy (Max - Min difference) to detect neutral/greyish backgrounds
                int maxV = Math.Max(areaBackground.R, Math.Max(areaBackground.G, areaBackground.B));
                int minV = Math.Min(areaBackground.R, Math.Min(areaBackground.G, areaBackground.B));
                int vibrancy = maxV - minV;

                // 3. Calculate Contrast-Safe Text Colors using APCA
                Color headerColor, descriptionColor;

                if (vibrancy < 30)
                {
                    // Neutral background (Grey/White/Black): Pick pure White or Dark-Grey for maximum clarity
                    double whiteLc = Math.Abs(ImageHelper.GetContrastAPCA(Color.FromArgb(255, 255, 255, 255), areaBackground));
                    double darkLc = Math.Abs(ImageHelper.GetContrastAPCA(Color.FromArgb(255, 20, 20, 22), areaBackground));

                    bool useDarkText = darkLc > (whiteLc + 15) && darkLc > 50;

                    if (!useDarkText)
                    {
                        headerColor = Color.FromArgb(255, 255, 255, 255);
                        descriptionColor = Color.FromArgb(255, 224, 224, 224);
                    }
                    else
                    {
                        headerColor = Color.FromArgb(255, 20, 20, 22);
                        descriptionColor = Color.FromArgb(255, 55, 55, 60);
                    }
                }
                else
                {
                    // Colorful background: Use primary-tinted text with best APCA direction
                    headerColor = ImageHelper.GetContrastSafeColor(primary, areaBackground, 92);
                    descriptionColor = ImageHelper.GetContrastSafeColor(headerColor, areaBackground, 72);
                }

                // 4. Adaptive Cinematic Gradient Scaling (The "Just Enough" Logic)
                double rawLc = Math.Abs(ImageHelper.GetContrastAPCA(headerColor, areaBackground));
                double vibrancyBonus = Math.Clamp((vibrancy - 40) / 4.0, 0, 25);
                double lc = rawLc + vibrancyBonus;

                bool isDarkText = headerColor.R < 100;

                // Fade in the Ambience Gradients
                AnimateOpacity(LocalInfoGradient, (float)Math.Clamp(1.0 - (lc / 130.0), 0.15, 0.95), TimeSpan.FromMilliseconds(650));
                AnimateOpacity(ExtraReadabilityGradient, (float)Math.Clamp(0.8 - (lc / 130.0), 0.10, 0.75), TimeSpan.FromMilliseconds(650));
                AnimateOpacity(BottomReadabilityGradient, (float)Math.Clamp(1.0 - (lc / 130.0), 0.15, 0.95), TimeSpan.FromMilliseconds(650));

                // Apply colors to UI elements (Titles, Icons, Borders)
                if (IdentityControl != null && IdentityControl.TitleTextBlock != null) 
                    IdentityControl.TitleTextBlock.Foreground = new SolidColorBrush(headerColor);
                
                if (OverviewText != null) 
                    OverviewText.Foreground = new SolidColorBrush(descriptionColor);


                System.Diagnostics.Debug.WriteLine($"[INFO-AMBIENCE] TransitionTo: L({headerColor}) R({descriptionColor})");

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApplyAmbience] Error: {ex.Message}");
            }
        }
        private async void HeroImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                if (HeroShimmer != null)
                {
                    DispatcherQueue.TryEnqueue(() => {
                        HeroShimmer.Visibility = Visibility.Collapsed;
                    });
                }

                // [Senior] Reveal logic for late-loading backgrounds (Deferred Crossfade)
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(img);

                // If it's the active slot and was hidden, reveal it
                if (img.Opacity > 0.05)
                {
                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 1f);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                    visual.StartAnimation("Opacity", fadeIn);
                }

                await ExtractAndApplyAmbienceAsync(img, "image opened");
            }
        }

        private void HeroImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is Image img)
            {
                if (HeroShimmer != null) HeroShimmer.Visibility = Visibility.Collapsed;
                
                // MetaHub Failover Logic: Try alternative domain before giving up
                string? failingUrl = (img.Source as BitmapImage)?.UriSource?.ToString();
                if (!string.IsNullOrEmpty(failingUrl) && failingUrl.Contains("metahub.space"))
                {
                    int retryCount = (img.Tag as int?) ?? 0;
                    if (retryCount == -1) return;

                    if (retryCount >= 1)
                    {
                        img.Tag = -1; 
                    }
                    else
                    {
                        string retryUrl = failingUrl;
                        if (failingUrl.Contains("live.metahub.space"))
                            retryUrl = failingUrl.Replace("live.metahub.space", "images.metahub.space");
                        else if (failingUrl.Contains("images.metahub.space"))
                            retryUrl = failingUrl.Replace("images.metahub.space", "live.metahub.space");

                        if (retryUrl != failingUrl)
                        {
                            img.Tag = retryCount + 1;
                            img.Source = new BitmapImage(new Uri(retryUrl));
                            return; 
                        }
                    }
                }
                
                // If we have more images, try to move to the next one immediately
                if (_backdropUrls != null && _backdropUrls.Count > 1)
                {
                    img.Tag = 0; 
                    RotateBackdrop();
                }
                else
                {
                    string fallbackPoster = _unifiedMetadata?.PosterUrl ?? _item?.PosterUrl;
                    if (!string.IsNullOrWhiteSpace(fallbackPoster))
                    {
                        img.Tag = -1;
                        img.Source = new BitmapImage(new Uri(fallbackPoster));
                    }
                }
            }
        }

        private async void RotateBackdrop()
        {
            if (HeroImage == null || HeroImage2 == null || _backdropUrls == null || _backdropUrls.Count <= 1 || _isHeroTransitionInProgress)
            {
                return;
            }

            _currentBackdropIndex = (_currentBackdropIndex + 1) % _backdropUrls.Count;
            string nextImgUrl = _backdropUrls[_currentBackdropIndex];
            await PerformHeroCrossfadeAsync(nextImgUrl, 2.0);
        }
    }
}

