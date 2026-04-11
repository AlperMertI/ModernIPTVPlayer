using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using ModernIPTVPlayer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class SpotlightInjectRow : UserControl
    {
        private WebView2 _webView;
        // Shared WebView2 environment is now managed by WebView2Service
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private List<StremioMediaStream> _items = new List<StremioMediaStream>();
        private int _currentIndex = 0;
        private bool _isTrailerPlaying = false;
        private readonly List<string> _currentImageCandidates = new List<string>();
        private int _currentImageCandidateIndex = 0;
        private CompositionClip? _videoClip;
        private CompositionClip? _borderClip;
        private ContainerVisual? _videoVisual;

        public event EventHandler<(IMediaStream Stream, UIElement SourceElement, Microsoft.UI.Xaml.Media.ImageSource PreloadedLogo)> ItemClicked;
        public event EventHandler HeaderClicked;
        public event EventHandler<(IMediaStream Stream, UIElement SourceElement)> TrailerExpandRequested;

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register("IsExpanded", typeof(bool), typeof(SpotlightInjectRow), new PropertyMetadata(false, OnIsExpandedChanged));

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SpotlightInjectRow row)
            {
                bool isExpanded = (bool)e.NewValue;
                row.AnimateExpansion(isExpanded);

                // Update ExpandButton icon and tooltip
                if (row.ExpandButton != null && row.ExpandButton.Content is FontIcon icon)
                {
                    icon.Glyph = isExpanded ? "\uE73F" : "\uE740";
                    ToolTipService.SetToolTip(row.ExpandButton, isExpanded ? "Küçült" : "Genişlet");
                }
            }
        }

        private async void AnimateExpansion(bool expand)
        {
            double targetHeight = expand ? 700 : 400;
            double targetScale = expand ? 0.94 : 1.0;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(600));

            // Height animation
            var heightAnim = new DoubleAnimation
            {
                To = targetHeight,
                Duration = duration,
                EasingFunction = easing,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(heightAnim, ContainerBorder);
            Storyboard.SetTargetProperty(heightAnim, "Height");

            // ScaleX animation (Vortex/Flynn signature style)
            var widthAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(widthAnim, ContainerTransform);
            Storyboard.SetTargetProperty(widthAnim, "ScaleX");

            var sb = new Storyboard();
            sb.Children.Add(heightAnim);
            sb.Children.Add(widthAnim);
            sb.Begin();

            // Scroll this row to center of viewport when expanding
            if (expand)
            {
                await Task.Delay(80);
                this.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.5
                });
            }
        }

        public SpotlightInjectRow()
        {
            this.InitializeComponent();
            this.DataContextChanged += SpotlightInjectRow_DataContextChanged;
            this.EffectiveViewportChanged += SpotlightInjectRow_EffectiveViewportChanged;
            this.Unloaded += SpotlightInjectRow_Unloaded;
            FallbackImage.ImageFailed += FallbackImage_ImageFailed;
            
            // Apply initial clips on load and track size changes for both containers
            this.Loaded += (s, e) => UpdateClips();
            if (VideoContainer != null) VideoContainer.SizeChanged += (s, e) => UpdateClips();
            if (ContainerBorder != null) ContainerBorder.SizeChanged += (s, e) => UpdateClips();
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClips();
        }

        private void UpdateClips()
        {
            try
            {
                // 2. Composition Rounded Clip for Content and Frame
                if (ContainerBorder != null && ContainerGrid != null && VideoContainer != null)
                {
                    // Visuals to clip:
                    // 1. borderVisual: The overall frame (full size clip)
                    // 2. contentVisual: The content grid (image + video, inset clip)
                    var borderVisual = ElementCompositionPreview.GetElementVisual(ContainerBorder);
                    var contentVisual = ElementCompositionPreview.GetElementVisual(ContainerGrid);
                    var videoVisual = ElementCompositionPreview.GetElementVisual(VideoContainer);

                    if (borderVisual != null && contentVisual != null && videoVisual != null)
                    {
                        var compositor = borderVisual.Compositor;

                        // Border Clip (Full Size) - Prevents Backdrop bleed
                        if (_borderClip == null)
                        {
                            try
                            {
                                var geometry = compositor.CreateRoundedRectangleGeometry();
                                geometry.CornerRadius = new Vector2(20, 20);
                                _borderClip = compositor.CreateGeometricClip(geometry);
                                borderVisual.Clip = _borderClip;
                            }
                            catch { }
                        }

                        // Content Clip (Inset) - Prevents Image/Video bleed
                        if (_videoClip == null)
                        {
                            try
                            {
                                var geometry = compositor.CreateRoundedRectangleGeometry();
                                geometry.CornerRadius = new Vector2(20, 20);
                                _videoClip = compositor.CreateGeometricClip(geometry);
                                
                                // Apply to both ContentGrid (for image) and VideoContainer (for video)
                                contentVisual.Clip = _videoClip;
                                videoVisual.Clip = _videoClip;
                            }
                            catch { }
                        }

                        // Update sizes and offsets
                        if (_borderClip != null)
                        {
                            try
                            {
                                dynamic dClip = _borderClip;
                                if (dClip.Geometry != null)
                                {
                                    dClip.Geometry.Size = new Vector2((float)ContainerBorder.ActualWidth, (float)ContainerBorder.ActualHeight);
                                    dClip.Geometry.Offset = Vector2.Zero;
                                }
                            }
                            catch { }
                        }

                        if (_videoClip != null)
                        {
                            try
                            {
                                dynamic dClip = _videoClip;
                                if (dClip.Geometry != null)
                                {
                                    // 0px inset (clipping is now perfect since source artifacts are gone)
                                    float inset = 0.0f;
                                    float w = (float)ContainerBorder.ActualWidth;
                                    float h = (float)ContainerBorder.ActualHeight;
                                    
                                    if (w > inset * 2 && h > inset * 2)
                                    {
                                        dClip.Geometry.Size = new Vector2(w - (inset * 2), h - (inset * 2));
                                        dClip.Geometry.Offset = new Vector2(inset, inset);
                                    }
                                    else
                                    {
                                        dClip.Geometry.Size = new Vector2(w, h);
                                        dClip.Geometry.Offset = Vector2.Zero;
                                    }
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[Spotlight] UpdateClips Error: {ex.Message}");
            }
        }

        private void SpotlightInjectRow_Unloaded(object sender, RoutedEventArgs e)
        {
            FallbackImage.ImageFailed -= FallbackImage_ImageFailed;
            if (_lastSubscribedItem != null)
            {
                _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;
                _lastSubscribedItem = null;
            }
            CleanupWebView();
        }

        private string _pendingTrailerId = null;
        private bool _isInViewport = false;

        private async void SpotlightInjectRow_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            // Guard against disposal
            if (!this.IsLoaded) return;
            
            try
            {
                var viewport = args.EffectiveViewport;
                var componentHeight = sender.ActualHeight;

                // If more than 50% is visible
                bool isHighlyVisible = viewport.Height > (componentHeight * 0.5);

                if (isHighlyVisible && !_isInViewport)
                {
                    _isInViewport = true;
                    if (!string.IsNullOrEmpty(_pendingTrailerId) && _webView == null)
                    {
                        string ytId = ExtractYouTubeId(_pendingTrailerId);
                        if (!string.IsNullOrEmpty(ytId)) InitializeWebView(ytId);
                    }
                }
                else if (!isHighlyVisible && _isInViewport)
                {
                    _isInViewport = false;
                    CleanupWebView();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] ViewportChanged error: {ex.Message}");
            }
        }

        private async void SpotlightInjectRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            try
            {
                if (args.NewValue is CatalogRowViewModel vm && vm.Items != null && vm.Items.Count > 0)
                {
                    // Take up to 5 items for the carousel
                    _items = vm.Items.Take(5).ToList();
                    _currentIndex = 0;

                    // [PRE-LOAD LOGOS] Initial touch for cache
                    foreach (var item in _items)
                    {
                        if (!string.IsNullOrEmpty(item.LogoUrl))
                        {
                            _ = ImageHelper.GetCachedLogo(item.LogoUrl);
                        }
                    }
                    
                    if (_items.Count > 0)
                    {
                        UpdateUI();
                        AnimateInfoIn(true);
                        UpdateNavigationVisibility();
                        
                        // [FIX] 15GB RAM: NEVER trigger a trailer load on DataContextChanged.
                        // Wait for viewport/scroll to settle. The EffectiveViewportChanged handler 
                        // will fire when it's actually visible and trigger InitializeWebView.
                        _pendingTrailerId = _items[_currentIndex].TrailerUrl; 
                    }
                }
                else
                {
                    CleanupWebView();
                    _items.Clear();
                    UpdateNavigationVisibility();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] DataContextChanged error: {ex.Message}");
            }
        }


        private void UpdateNavigationVisibility()
        {
            if (PrevButton != null && NextButton != null)
            {
                bool hasMultiple = _items.Count > 1;
                PrevButton.Visibility = hasMultiple ? Visibility.Visible : Visibility.Collapsed;
                NextButton.Visibility = hasMultiple ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateUI()
        {
            if (_currentIndex < 0 || _currentIndex >= _items.Count) return;
            var item = _items[_currentIndex];
            
            SubscribeToItemChanges(item);

            if (!string.IsNullOrEmpty(item.LogoUrl))
            {
                LogoImage.Source = ImageHelper.GetCachedLogo(item.LogoUrl);
                LogoImage.Visibility = Visibility.Visible;
                TitleBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                LogoImage.Visibility = Visibility.Collapsed;
                TitleBlock.Visibility = Visibility.Visible;
                TitleBlock.Text = item.Title ?? "";
            }

            YearBlock.Text = item.Year ?? "";
            RatingBlock.Text = item.Rating ?? "";
            DescriptionBlock.Text = item.Meta?.Description ?? "";

            BuildImageCandidates(item);
            _currentImageCandidateIndex = 0;
            TrySetCurrentImageCandidate();
            
            FallbackImage.Opacity = 1;

            string idToCheck = item.IMDbId ?? item.Id.ToString();
            if (Services.WatchlistManager.Instance.IsOnWatchlist(idToCheck))
            {
                WatchlistButton.Content = new FontIcon { Glyph = "\xE73E", FontSize = 16 };
            }
            else
            {
                WatchlistButton.Content = new FontIcon { Glyph = "\xE710", FontSize = 16 };
            }

            System.Diagnostics.Debug.WriteLine($"[Spotlight] UI Updated for: {item.Title} (ID: {item.IMDbId}) | Logo: {!string.IsNullOrEmpty(item.LogoUrl)} | Desc: {(!string.IsNullOrEmpty(DescriptionBlock.Text) ? "Yes" : "No")}");

            // Smart Pre-load: Decode next/prev logos
            DispatcherQueue.TryEnqueue(() => 
            {
                try
                {
                    if (_items != null && _items.Count > 1)
                    {
                        int next = (_currentIndex + 1) % _items.Count;
                        int prev = (_currentIndex - 1 + _items.Count) % _items.Count;
                        
                        // We set it to the preloader to force decode
                        var nextBmp = ImageHelper.GetCachedLogo(_items[next].LogoUrl);
                        if (nextBmp != null) LogoPreloader.Source = nextBmp;
                        
                        var prevBmp = ImageHelper.GetCachedLogo(_items[prev].LogoUrl);
                        if (prevBmp != null) LogoPreloader.Source = prevBmp;
                    }
                }
                catch { /* Ignore pre-load errors */ }
            });
        }

        private Task AnimateInfoOut(bool isNext)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (InfoPanel == null || InfoTransform == null || InfoPanel.XamlRoot == null)
            {
                tcs.SetResult(true);
                return tcs.Task;
            }

            try
            {
                var sb = new Storyboard();
                
                var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                var slide = new DoubleAnimation { To = isNext ? -50 : 50, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                
                Storyboard.SetTarget(fade, InfoPanel);
                Storyboard.SetTargetProperty(fade, "Opacity");
                Storyboard.SetTarget(slide, InfoTransform);
                Storyboard.SetTargetProperty(slide, "TranslateX");
                
                sb.Children.Add(fade);
                sb.Children.Add(slide);
                sb.Completed += (s, e) => tcs.TrySetResult(true);
                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] AnimateOut Error: {ex.Message}");
                tcs.TrySetResult(true);
            }
            
            return tcs.Task;
        }

        private void AnimateInfoIn(bool isNext)
        {
            if (InfoPanel == null || InfoTransform == null || InfoPanel.XamlRoot == null) return;

            try
            {
                InfoTransform.TranslateX = isNext ? 50 : -50;
                InfoPanel.Opacity = 0;
                
                var sb = new Storyboard();
                var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                var slide = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                
                Storyboard.SetTarget(fade, InfoPanel);
                Storyboard.SetTargetProperty(fade, "Opacity");
                Storyboard.SetTarget(slide, InfoTransform);
                Storyboard.SetTargetProperty(slide, "TranslateX");
                
                sb.Children.Add(fade);
                sb.Children.Add(slide);
                sb.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] AnimateIn Error: {ex.Message}");
                // Fallback to instant visibility
                InfoPanel.Opacity = 1;
                InfoTransform.TranslateX = 0;
            }
        }

        private StremioMediaStream _lastSubscribedItem = null;

        private void SubscribeToItemChanges(StremioMediaStream item)
        {
            if (_lastSubscribedItem == item) return;

            if (_lastSubscribedItem != null)
                _lastSubscribedItem.PropertyChanged -= Item_PropertyChanged;

            _lastSubscribedItem = item;
            if (_lastSubscribedItem != null)
                _lastSubscribedItem.PropertyChanged += Item_PropertyChanged;
        }

        private void Item_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is StremioMediaStream item)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    // Refresh fields if they might have been enriched
                    if (e.PropertyName == nameof(item.Title) || e.PropertyName == nameof(item.Description) || 
                        e.PropertyName == nameof(item.Rating) || e.PropertyName == nameof(item.Year) || 
                        e.PropertyName == nameof(item.Genres) || e.PropertyName == nameof(item.LogoUrl))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Spotlight] Enrichment update received for {item.Title} ({e.PropertyName})");
                        UpdateUI();
                    }

                    if (e.PropertyName == nameof(item.Banner) || e.PropertyName == nameof(item.BackdropUrl))
                    {
                        BuildImageCandidates(item);
                        _currentImageCandidateIndex = 0;
                        TrySetCurrentImageCandidate();
                    }
                });
            }
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            CleanupWebView();
        }

        private async Task TryLoadTrailerAsync()
        {
            try
            {
                if (_items.Count == 0 || _currentIndex >= _items.Count) return;
                var currentItem = _items[_currentIndex];
                
                // [OPTIMIZATION] Smooth cross-fade instead of binary collapse
                _pendingTrailerId = null;
                _isTrailerPlaying = false;
                
                if (VideoContainer != null)
                {
                    var fadeOut = new Storyboard();
                    var exitAnim = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                    Storyboard.SetTarget(exitAnim, VideoContainer);
                    Storyboard.SetTargetProperty(exitAnim, "Opacity");
                    fadeOut.Children.Add(exitAnim);
                    fadeOut.Begin();
                }

                if (ExpandButton != null) ExpandButton.Visibility = Visibility.Collapsed;
                if (MuteButton != null) MuteButton.Visibility = Visibility.Collapsed;

                if (_webView?.CoreWebView2 != null)
                {
                    try { await _webView.CoreWebView2.ExecuteScriptAsync("if(typeof stopVideo === 'function') stopVideo();"); } 
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Spotlight] stopVideo script error: {ex.Message}"); }
                }

                string trailerId = null;

                // 1. [FAST-PATH] Try existing trailers in meta to switch IMMEDIATELY
                if (currentItem.Meta?.Trailers != null && currentItem.Meta.Trailers.Count > 0)
                {
                    trailerId = currentItem.Meta.Trailers.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Source))?.Source;
                }

                if (!string.IsNullOrEmpty(trailerId))
                {
                    string ytId = ExtractYouTubeId(trailerId);
                    if (!string.IsNullOrEmpty(ytId))
                    {
                        await StartOrSwitchVideoAsync(ytId);
                    }
                }

                // 2. Refresh metadata in background to get BETTER trailers if needed
                Models.Metadata.UnifiedMetadata? discoveryUnified = null;
                try
                {
                    discoveryUnified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(currentItem, Models.Metadata.MetadataContext.Discovery);
                    if (currentItem != _items[_currentIndex]) return; // Check if item changed during await

                    if (discoveryUnified != null)
                    {
                        ApplyUnifiedToSpotlightItem(currentItem, discoveryUnified);
                        if (!string.IsNullOrEmpty(discoveryUnified.TrailerUrl))
                        {
                            string newTrailerId = discoveryUnified.TrailerUrl;
                            if (newTrailerId != trailerId) // Only switch if it's a different trailer
                            {
                                trailerId = newTrailerId;
                                string ytId = ExtractYouTubeId(trailerId);
                                if (!string.IsNullOrEmpty(ytId))
                                {
                                    await StartOrSwitchVideoAsync(ytId);
                                    return; // Success, found and started a new trailer
                                }
                            }
                            else if (!string.IsNullOrEmpty(trailerId)) 
                            {
                                return; // Already started via fast-path with the same trailer
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Discovery meta fetch error: {ex.Message}");
                }

                // 3. Trailer hala yoksa Detail metadata dene.
                if (string.IsNullOrEmpty(trailerId))
                {
                    try
                    {
                        var detailUnified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(currentItem, Models.Metadata.MetadataContext.Spotlight);
                        if (currentItem != _items[_currentIndex]) return; // Check if item changed during await

                        if (detailUnified != null)
                        {
                            ApplyUnifiedToSpotlightItem(currentItem, detailUnified);
                            if (!string.IsNullOrEmpty(detailUnified.TrailerUrl))
                            {
                                string newTrailerId = detailUnified.TrailerUrl;
                                if (newTrailerId != trailerId)
                                {
                                    trailerId = newTrailerId;
                                    string ytId = ExtractYouTubeId(trailerId);
                                    if (!string.IsNullOrEmpty(ytId))
                                    {
                                        await StartOrSwitchVideoAsync(ytId);
                                        return; // Success
                                    }
                                }
                                else if (!string.IsNullOrEmpty(trailerId))
                                {
                                    return; // Already started via fast-path or discovery with the same trailer
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Spotlight] Detail meta fetch error: {ex.Message}");
                    }
                }
                
                if (currentItem != _items[_currentIndex]) return;
                // If we reach here and still no trailerId, hide video
                if (string.IsNullOrEmpty(trailerId))
                {
                    CleanupWebView();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] LoadTrailer Error: {ex.Message}");
                CleanupWebView();
            }
        }

        private async Task StartOrSwitchVideoAsync(string ytId)
        {
            if (string.IsNullOrEmpty(ytId) || !_isInViewport) return;
            
            _pendingTrailerId = ytId;

            if (_webView == null)
            {
                InitializeWebView(ytId);
            }
            else if (_webView.CoreWebView2 != null)
            {
                // Optimization: Switch video within same WebView
                try 
                { 
                    await _webView.CoreWebView2.ExecuteScriptAsync($"if(typeof switchVideo === 'function') switchVideo('{ytId}');"); 
                } 
                catch (Exception ex) 
                { 
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] switchVideo script error: {ex.Message}"); 
                }
                UpdateMuteButtonIcon();
            }
        }

        private void BuildImageCandidates(StremioMediaStream item)
        {
            _currentImageCandidates.Clear();
            AddImageCandidate(item?.BackdropUrl);
            AddImageCandidate(item?.Meta?.Background);
            AddImageCandidate(item?.LandscapeImageUrl);
            AddImageCandidate(item?.Banner);
            AddImageCandidate(item?.PosterUrl);
            AddImageCandidate(item?.Meta?.Poster);
        }

        private void AddImageCandidate(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            string candidate = url.Trim();
            if (string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, "none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate.Substring("http://".Length);
            }

            // Upgrade MetaHub quality
            if (candidate.Contains("metahub.space/"))
            {
                candidate = candidate.Replace("/medium/", "/large/").Replace("/small/", "/large/");
            }

            if (_currentImageCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return;
            _currentImageCandidates.Add(candidate);
        }

        private void TrySetCurrentImageCandidate()
        {
            while (_currentImageCandidateIndex < _currentImageCandidates.Count)
            {
                string candidate = _currentImageCandidates[_currentImageCandidateIndex];
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Invalid image URL skipped: {candidate}");
                    _currentImageCandidateIndex++;
                    continue;
                }

                FallbackImage.Source = new BitmapImage(uri);
                return;
            }

            FallbackImage.Source = null;
            System.Diagnostics.Debug.WriteLine("[Spotlight] No usable image candidate found.");
        }

        private void FallbackImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string failed = (_currentImageCandidateIndex >= 0 && _currentImageCandidateIndex < _currentImageCandidates.Count)
                ? _currentImageCandidates[_currentImageCandidateIndex]
                : "unknown";
            System.Diagnostics.Debug.WriteLine($"[Spotlight] Image failed: {failed} | Error={e?.ErrorMessage}");
            _currentImageCandidateIndex++;
            TrySetCurrentImageCandidate();
        }
        private void ApplyUnifiedToSpotlightItem(StremioMediaStream item, Models.Metadata.UnifiedMetadata unified)
        {
            if (item?.Meta == null || unified == null) return;

            // Preserve original catalog title for dual-title use in detail page if TMDB is about to change it
            if (!string.IsNullOrWhiteSpace(unified.Title) && item.Meta.Name != unified.Title)
            {
                if (string.IsNullOrWhiteSpace(item.Meta.OriginalName) &&
                    !string.IsNullOrWhiteSpace(item.Meta.Name) &&
                    !string.Equals(item.Meta.Name, unified.Title, StringComparison.OrdinalIgnoreCase))
                {
                    item.Meta.OriginalName = item.Meta.Name;
                }
            }

            item.UpdateFromUnified(unified);

            if (item == _items[_currentIndex])
            {
                item.OnPropertyChanged(nameof(item.LandscapeImageUrl));
                UpdateUI();
            }
        }

        private string ExtractYouTubeId(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            if (!source.Contains("/") && !source.Contains(".")) return source;

            try
            {
                if (source.Contains("v="))
                {
                    var split = source.Split("v=");
                    if (split.Length > 1) return split[1].Split('&')[0];
                }
                else if (source.Contains("be/"))
                {
                    var split = source.Split("be/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                }
                else if (source.Contains("embed/"))
                {
                    var split = source.Split("embed/");
                    if (split.Length > 1) return split[1].Split('?')[0];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] ExtractYouTubeId error: {ex.Message}");
            }

            return source;
        }

        private async void InitializeWebView(string ytId)
        {
            // Guard against disposal
            if (!this.IsLoaded || _webView != null || _isTrailerPlaying) return;

            try
            {
                // 0. Ensure environment is ready (Managed by WebView2Service)
                var env = await WebView2Service.GetSharedEnvironmentAsync();

                // Re-check after await
                if (!this.IsLoaded || _webView != null) return;

                _webView = new WebView2();
                _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                _webView.VerticalAlignment = VerticalAlignment.Stretch;
                
                if (VideoContainer != null) 
                {
                    // Adding to the grid
                    VideoContainer.Children.Add(_webView);
                }
                else
                {
                    _webView = null;
                    return;
                }

                await _webView.EnsureCoreWebView2Async(env);
                if (!this.IsLoaded || _webView == null || _webView.CoreWebView2 == null) return;

                // [FIX] Apply 100% Clean UI Script (Hides Title Flash, Logos, More Videos)
                await WebView2Service.ApplyYouTubeCleanUISettingsAsync(_webView.CoreWebView2);

                // Apply composition clip immediately after UI tree changes
                UpdateClips();
                
                string virtualHost = $"spotlight-{_instanceId}.moderniptv.local";
                string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTV_Spotlight", _instanceId);
                System.IO.Directory.CreateDirectory(tempDir);
                
                string htmlContent = $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        html, body {{ width: 100%; height: 100%; background: #000; overflow: hidden; }}
        #player {{ 
            position: absolute; top: 50%; left: 50%;
            min-width: 177.77vh; min-height: 56.25vw;
            width: 100vw; height: 56.25vw; transform: translate(-50%, -50%);
        }}
    </style>
</head>
<body>
    <div id='player'></div>
    <script>
        var tag = document.createElement('script');
        tag.src = 'https://www.youtube.com/iframe_api';
        var firstScriptTag = document.getElementsByTagName('script')[0];
        firstScriptTag.parentNode.insertBefore(tag, firstScriptTag);

        var player;
        function onYouTubeIframeAPIReady() {{
            player = new YT.Player('player', {{
                height: '100%', width: '100%',
                videoId: '{ytId}', 
                host: 'https://www.youtube.com',
                playerVars: {{
                    autoplay: 1, mute: 1, controls: 0, disablekb: 1,
                    fs: 0, rel: 0, modestbranding: 1, showinfo: 0,
                    iv_load_policy: 3, playsinline: 1, loop: 1, playlist: '{ytId}'
                }},
                events: {{
                    'onReady': function(e) {{ 
                        e.target.mute(); 
                        e.target.playVideo(); 
                        try {{ window.chrome.webview.postMessage('READY'); }} catch(ex) {{}}
                    }},
                    'onStateChange': function(e) {{ 
                        if (e.data === 1) {{ // Playing
                            if (window._stallTimer) {{ 
                                clearTimeout(window._stallTimer); 
                                window._stallTimer = null; 
                            }}
                            try {{ window.chrome.webview.postMessage('READY'); }} catch(ex) {{}}
                        }} else if (e.data === -1 || e.data === 3) {{ // Unstarted / Buffering
                            if (!window._stallTimer) {{
                                window._stallTimer = setTimeout(function() {{
                                    try {{ window.chrome.webview.postMessage('ERROR'); }} catch(ex) {{}}
                                }}, 10000); // 10 seconds for robustness
                            }}
                        }}
                    }},
                    'onError': function(e) {{
                        if (window._stallTimer) {{ 
                            clearTimeout(window._stallTimer); 
                            window._stallTimer = null; 
                        }}
                        try {{ window.chrome.webview.postMessage('ERROR'); }} catch(ex) {{}}
                    }}
                }}
            }});
        }}

        function switchVideo(newId) {{
            if (window._stallTimer) {{ 
                clearTimeout(window._stallTimer); 
                window._stallTimer = null; 
            }}
            if (player && player.loadVideoById) {{
                player.loadVideoById({{'videoId': newId, 'startSeconds': 0}});
                player.playVideo();
                // Send READY early for smoother UI if switching rapidly
                try {{ window.chrome.webview.postMessage('READY'); }} catch(ex) {{}}
            }}
        }}

        function stopVideo() {{
            if (player && player.stopVideo) {{
                player.stopVideo();
            }}
        }}
    </script>
</body>
</html>";
                
                string htmlPath = System.IO.Path.Combine(tempDir, "index.html");
                await System.IO.File.WriteAllTextAsync(htmlPath, htmlContent);
                
                if (_webView == null || _webView.CoreWebView2 == null) return;

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    virtualHost, tempDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.Source = new Uri($"https://{virtualHost}/index.html");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] WebView Error: {ex.Message}");
                CleanupWebView();
            }
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string msg = args.TryGetWebMessageAsString();
                if (msg == "READY")
                {
                    _isTrailerPlaying = true;
                    
                    if (VideoContainer != null) 
                    {
                        VideoContainer.Visibility = Visibility.Visible;
                        UpdateMuteButtonIcon();

                        // Smooth Fade-In Reveal
                        var sb = new Storyboard();
                        var anim = new DoubleAnimation 
                        { 
                            To = 0.8, 
                            Duration = TimeSpan.FromMilliseconds(1500), 
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } 
                        }; 
                        Storyboard.SetTarget(anim, VideoContainer);
                        Storyboard.SetTargetProperty(anim, "Opacity");
                        sb.Children.Add(anim);
                        sb.Begin();
                    }
                    
                    if (ExpandButton != null) ExpandButton.Visibility = Visibility.Visible;
                    if (MuteButton != null) MuteButton.Visibility = Visibility.Visible;
                }
                else if (msg == "ERROR")
                {
                    System.Diagnostics.Debug.WriteLine("[Spotlight] Trailer error reported by WebView. Cleaning up and falling back to backdrop.");
                    CleanupWebView();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] WebMessageReceived error: {ex.Message}");
            }
        }

        private void CleanupWebView()
        {
            _pendingTrailerId = null;
            if (_webView != null)
            {
                try
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                        _webView.CoreWebView2.Stop();
                    }
                    
                    if (VideoContainer != null && VideoContainer.Children.Contains(_webView))
                    {
                        VideoContainer.Children.Remove(_webView);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] WebView parent removal error: {ex.Message}");
                }
                
                _webView = null;
                _isTrailerPlaying = false;
                
                if (ExpandButton != null) ExpandButton.Visibility = Visibility.Collapsed;
                if (MuteButton != null) MuteButton.Visibility = Visibility.Collapsed;

                try
                {
                    string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTV_Spotlight", _instanceId);
                    if (System.IO.Directory.Exists(tempDir))
                        System.IO.Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Temp directory cleanup error: {ex.Message}");
                }
            }
            if (VideoContainer != null) VideoContainer.Opacity = 0;
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_items.Count <= 1) return;
                
                await AnimateInfoOut(false);
                
                _currentIndex--;
                if (_currentIndex < 0) _currentIndex = _items.Count - 1;
                
                UpdateUI();
                AnimateInfoIn(false);
                
                await TryLoadTrailerAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] PrevButton Error: {ex.Message}");
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_items.Count <= 1) return;
                
                await AnimateInfoOut(true);
                
                _currentIndex++;
                if (_currentIndex >= _items.Count) _currentIndex = 0;
                
                UpdateUI();
                AnimateInfoIn(true);
                
                await TryLoadTrailerAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] NextButton Error: {ex.Message}");
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                var item = _items[_currentIndex];
                ItemClicked?.Invoke(this, (item, FallbackImage, LogoImage.Source));
            }
        }

        private void HeaderLink_Click(object sender, RoutedEventArgs e)
        {
            ElementSoundPlayer.Play(ElementSoundKind.Invoke);
            HeaderClicked?.Invoke(this, EventArgs.Empty);
        }

        private async void WatchlistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                var item = _items[_currentIndex];
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);

                // Fetch full unified metadata to get canonical ID
                Models.Metadata.UnifiedMetadata? unified = null;
                try
                {
                    unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Watchlist meta fetch error: {ex.Message}");
                }

                string idToSave = unified?.ImdbId ?? item.IMDbId ?? item.Id.ToString();
                string titleToSave = unified?.Title ?? item.Title;
                string typeToSave = unified?.IsSeries == true ? "series" : (item.Meta?.Type ?? "movie");
                string posterToSave = unified?.PosterUrl ?? item.PosterUrl;

                if (!string.IsNullOrEmpty(idToSave) && !string.IsNullOrEmpty(titleToSave))
                {
                    if (Services.WatchlistManager.Instance.IsOnWatchlist(idToSave))
                    {
                        await Services.WatchlistManager.Instance.RemoveFromWatchlist(idToSave);
                        WatchlistButton.Content = new FontIcon { Glyph = "\xE710", FontSize = 16 };
                    }
                    else
                    {
                        await Services.WatchlistManager.Instance.AddToWatchlist(item);
                        WatchlistButton.Content = new FontIcon { Glyph = "\xE73E", FontSize = 16 };
                    }
                }
            }
        }

        private bool _isMuted = true;

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    _isMuted = !_isMuted;
                    string script = _isMuted ? "if(typeof player !== 'undefined') player.mute();" : "if(typeof player !== 'undefined') player.unMute();";
                    await _webView.CoreWebView2.ExecuteScriptAsync(script);
                    UpdateMuteButtonIcon();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] Mute Error: {ex.Message}");
            }
        }

        private void UpdateMuteButtonIcon()
        {
            if (MuteIcon != null)
            {
                MuteIcon.Glyph = _isMuted ? "\xE74F" : "\xE767"; // Mute / Volume icon
            }
        }

        private void ExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                IsExpanded = !IsExpanded;
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] Expand Error: {ex.Message}");
            }
        }
    }
}
