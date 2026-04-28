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
using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class SpotlightInjectRow : UserControl
    {
        // PROJECT ZERO: Shared WebView2 handled via TrailerPoolService.
        private WebView2? _webView; 
        // Shared WebView2 environment is now managed by WebView2Service
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private List<StremioMediaStream> _items = new List<StremioMediaStream>();
        private int _currentIndex = 0;
        private bool _isTrailerPlaying = false;
        private readonly System.Threading.SemaphoreSlim _playLock = new System.Threading.SemaphoreSlim(1, 1);
        private readonly List<string> _currentImageCandidates = new List<string>();
        private int _currentImageCandidateIndex = 0;
        private CompositionClip? _videoClip;
        private CompositionClip? _borderClip;
        private ContainerVisual? _videoVisual;

        public event EventHandler<SpotlightItemClickedEventArgs> ItemClicked;
        public event EventHandler HeaderClicked;
        public event EventHandler<TrailerExpandRequestedEventArgs> TrailerExpandRequested;

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
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("SpotlightInjectRow.xaml.cs:ctor", "enter", null, "H-RENDER"); } catch { }
            // #endregion
            this.InitializeComponent();
            // #region agent log
            try { ModernIPTVPlayer.App.DebugNdjson("SpotlightInjectRow.xaml.cs:ctor", "InitializeComponent done", null, "H-RENDER"); } catch { }
            // #endregion
            this.Loaded += UserControl_Loaded;
            this.Unloaded += UserControl_Unloaded;

            // [SENIOR REACTIVE FIX] Listen for global metadata updates. 
            // When the background enrichment worker finds a trailer for our current item, 
            // we catch the signal and trigger playback immediately.
            StremioMediaStream.OnMetadataUpdated += HandleGlobalMetadataUpdated;

            this.DataContextChanged += SpotlightInjectRow_DataContextChanged;
            this.EffectiveViewportChanged += SpotlightInjectRow_EffectiveViewportChanged;
            this.SizeChanged += (s, e) => { UpdateClips(); SynchronizeViewport(); };
            FallbackImage.ImageFailed += FallbackImage_ImageFailed;
            
            // [STREMIO_DEBUG] Subscriptions moved to Loaded/Unloaded
            if (VideoContainer != null) VideoContainer.SizeChanged += (s, e) => UpdateClips();
            if (ContainerBorder != null) ContainerBorder.SizeChanged += (s, e) => UpdateClips();
        }

        private void HandleGlobalMetadataUpdated(string id)
        {
            // [SENIOR GUARD] Only react if this row is currently visible AND the updated ID matches our current spotlight item.
            if (!_isInViewport || _items == null || _currentIndex >= _items.Count) return;

            var currentItem = _items[_currentIndex];
            if (currentItem.IMDbId == id || currentItem.Meta?.Id == id)
            {
                // Must be on UI thread for UI updates and WebView acquisition
                DispatcherQueue.TryEnqueue(async () => 
                {
                    // Refresh UI fields (Description, Genres, etc.) that might have been updated
                    UpdateUI();

                    // Trigger trailer load now that we have fresh metadata
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Metadata updated for {currentItem.Title}. Triggering trailer load.");
                    await TryLoadTrailerAsync();
                });
            }
        }

        private void Instance_TrailerMessageReceived(object? sender, string e)
        {
            // [STREMIO_DEBUG: OWNERSHIP GUARD]
            // Critically important: Only the row that actually has the WebView inside its VideoContainer 
            // should react to READY messages. This prevents "zombie" reveals.
            if (TrailerPoolService.Instance.CurrentContainer != VideoContainer)
            {
                return; 
            }

            if (e.StartsWith("READY"))
            {
                OnTrailerMessageReceived(e);
            }
        }

        private void LogoImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (LogoImage.Source is BitmapImage bi)
            {
                if (bi.PixelWidth > 0 && bi.PixelWidth < 10 && bi.PixelHeight < 10)
                {
                    if (_currentIndex >= 0 && _currentIndex < _items.Count)
                    {
                        var item = _items[_currentIndex];
                        item.LogoLoadFailed = true;
                    }
                    LogoImage.Visibility = Visibility.Collapsed;
                    TitleBlock.Visibility = Visibility.Visible;
                    return;
                }
            }

            // [SUCCESS] We have a real logo. Hide the title fallback.
            TitleBlock.Visibility = Visibility.Collapsed;
            LogoImage.Visibility = Visibility.Visible;
        }

        private void LogoImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                var item = _items[_currentIndex];
                item.LogoLoadFailed = true;
                LogoImage.Visibility = Visibility.Collapsed;
                TitleBlock.Visibility = Visibility.Visible;
            }
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
                                System.Diagnostics.Debug.WriteLine("[SPOTLIGHT_VIEW_DEBUG] Border clip created.");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Spotlight] Border clip creation error: {ex.GetType().Name} | {ex.Message}");
                            }
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
                                System.Diagnostics.Debug.WriteLine("[SPOTLIGHT_VIEW_DEBUG] Video clip created and applied.");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Spotlight] Video clip creation error: {ex.GetType().Name} | {ex.Message}");
                            }
                        }

                        // Update sizes and offsets
                        if (_borderClip is CompositionGeometricClip borderGeometricClip && 
                            borderGeometricClip.Geometry is CompositionRoundedRectangleGeometry borderGeometry)
                        {
                            try
                            {
                                // [FIX] If layout hasn't finished, don't clip everything away
                                if (ContainerBorder.ActualWidth <= 0 || ContainerBorder.ActualHeight <= 0)
                                {
                                    ContainerGrid.Clip = null;
                                    return;
                                }

                                borderGeometry.Size = new Vector2((float)ContainerBorder.ActualWidth, (float)ContainerBorder.ActualHeight);
                                borderGeometry.Offset = Vector2.Zero;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Spotlight] Border clip update error: {ex.GetType().Name} | {ex.Message}");
                            }
                        }

                        if (_videoClip is CompositionGeometricClip videoGeometricClip && 
                            videoGeometricClip.Geometry is CompositionRoundedRectangleGeometry videoGeometry)
                        {
                            try
                            {
                                // 0px inset (clipping is now perfect since source artifacts are gone)
                                float inset = 0.0f;
                                float w = (float)ContainerBorder.ActualWidth;
                                float h = (float)ContainerBorder.ActualHeight;
                                
                                if (w > inset * 2 && h > inset * 2)
                                {
                                    videoGeometry.Size = new Vector2(w - (inset * 2), h - (inset * 2));
                                    videoGeometry.Offset = new Vector2(inset, inset);
                                }
                                else
                                {
                                    videoGeometry.Size = new Vector2(w, h);
                                    videoGeometry.Offset = Vector2.Zero;
                                }
                            }
                            catch (Exception) { }
                        }
                        
                        // [PRECISION LOG] Trace actual clipping sizes
                        if (_videoClip is CompositionGeometricClip vgc && vgc.Geometry is CompositionRoundedRectangleGeometry vrg)
                        {
                             // System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_VIEW_DEBUG] Current Clip Size: {vrg.Size.X}x{vrg.Size.Y}");
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

            if (_lastItemsCollection != null)
            {
                if (_lastItemsCollection is System.Collections.Specialized.INotifyCollectionChanged notify)
                {
                    notify.CollectionChanged -= Items_CollectionChanged;
                }
                _lastItemsCollection = null;
            }

            if (_lastVm != null)
            {
                _lastVm.PropertyChanged -= Vm_PropertyChanged;
                _lastVm = null;
            }

            _debounceTimer?.Stop();
            _debounceTimer = null;

            TrailerPoolService.Instance.TrailerMessageReceived -= Instance_TrailerMessageReceived;
            CleanupWebView();
            _isInViewport = false;
        }

        private string _pendingTrailerId = null;
        private bool _isInViewport = false;

        private System.Collections.IList? _lastItemsCollection = null;
        private CatalogRowViewModel? _lastVm = null;
        private DispatcherTimer? _debounceTimer;

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Items" && sender is CatalogRowViewModel vm)
            {
                DispatcherQueue.TryEnqueue(() => 
                {
                    SwitchItemsCollection(vm.Items);
                    RefreshStateFromViewModel(vm);
                });
            }
        }

        private void SwitchItemsCollection(System.Collections.IList? newCollection)
        {
            if (_lastItemsCollection is System.Collections.Specialized.INotifyCollectionChanged oldNotify)
            {
                oldNotify.CollectionChanged -= Items_CollectionChanged;
            }
            _lastItemsCollection = newCollection;
            if (_lastItemsCollection is System.Collections.Specialized.INotifyCollectionChanged newNotify)
            {
                newNotify.CollectionChanged += Items_CollectionChanged;
            }
        }

        private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                if (DataContext is CatalogRowViewModel vm && vm.Items != null && vm.Items.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] CollectionChanged detected. Items: {vm.Items.Count}");
                    RefreshStateFromViewModel(vm);
                }
            });
        }

        private async void SpotlightInjectRow_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            // Guard against disposal
            if (!this.IsLoaded) return;
            
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SynchronizeViewport();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] ViewportChanged error: {ex.Message}");
            }
        }

        /// <summary>
        /// [STREMIO_DEBUG: NATIVE ARCHITECTURE]
        /// Manually synchronizes the viewport state with WinUI's actual rendering state.
        /// This is the proper way to handle initial visibility in virtualized repeaters.
        /// </summary>
        private DispatcherTimer? _syncDebounceTimer;

        public void SynchronizeViewport()
        {
            if (this.XamlRoot == null) return;

            if (_syncDebounceTimer == null)
            {
                _syncDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                _syncDebounceTimer.Tick += (s, e) =>
                {
                    _syncDebounceTimer.Stop();
                    ExecuteSync();
                };
            }
            _syncDebounceTimer.Stop();
            _syncDebounceTimer.Start();
        }

        private void ExecuteSync()
        {
            if (this.XamlRoot == null) return;
            
            try
            {
                var transform = this.TransformToVisual(this.XamlRoot.Content);
                var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
                var hostHeight = this.XamlRoot.Size.Height;

                // [FIX] Robust height calculation for initial layout pass
                double targetHeight = ActualHeight > 0 ? ActualHeight : 400;

                double visibleHeight = Math.Min(bounds.Y + bounds.Height, hostHeight) - Math.Max(bounds.Y, 0);
                bool isHighlyVisible = visibleHeight > (targetHeight * 0.5);
                
                bool isActualOwner = TrailerPoolService.Instance.CurrentContainer == VideoContainer;
                
                // [LOG SILENCE] Only log if we are visible OR if the state is changing.
                // This prevents off-screen virtualized rows from flooding the logs.
                if (isHighlyVisible || isHighlyVisible != _isInViewport)
                {
                    string movieTitle = _items.Count > _currentIndex ? _items[_currentIndex].Title : "Unknown";
                    System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_TRACE] {movieTitle} | Y={bounds.Y:F0} | Visible={isHighlyVisible} | Owner={isActualOwner} | InVP={_isInViewport}");
                }

                // [SENIOR: SELF-HEALING STATE]
                // We should trigger a load if we are visible AND 
                // (we weren't before OR we don't currently own the webview OR nothing is playing yet)
                if (isHighlyVisible && (!_isInViewport || !isActualOwner || !_isTrailerPlaying))
                {
                    _isInViewport = true;
                    _ = TryLoadTrailerAsync();
                }
                else if (!isHighlyVisible && _isInViewport)
                {
                    _isInViewport = false;
                    CleanupWebView();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_ENGINE] Sync Error: {ex.Message}");
            }
        }

        private void SpotlightInjectRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            CleanupWebView();
            _isInViewport = false; // Reset to force fresh sync
            try
            {
                _pendingTrailerId = null;

                if (_lastVm != null)
                {
                    _lastVm.PropertyChanged -= Vm_PropertyChanged;
                    _lastVm = null;
                }

                if (args.NewValue is CatalogRowViewModel vm)
                {
                    _lastVm = vm;
                    _lastVm.PropertyChanged += Vm_PropertyChanged;

                    SwitchItemsCollection(vm.Items);
                    RefreshStateFromViewModel(vm);
                }
                else
                {
                    SwitchItemsCollection(null);
                    _items.Clear();
                    UpdateNavigationVisibility();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] DataContextChanged error: {ex.Message}");
            }
        }

        private void RefreshStateFromViewModel(CatalogRowViewModel vm)
        {
            if (vm.Items != null && vm.Items.Count > 0)
            {
                var newItems = vm.Items.OfType<StremioMediaStream>().Take(5).ToList();
                
                // [PERF] Skip if items are identical (prevents double-update during Discovery hydration)
                if (_items != null && _items.Count == newItems.Count)
                {
                    bool identical = true;
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if ((_items[i].IMDbId ?? _items[i].Id.ToString()) != (newItems[i].IMDbId ?? newItems[i].Id.ToString()))
                        {
                            identical = false;
                            break;
                        }
                    }
                    if (identical) return;
                }

                _items = newItems;
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
                    SynchronizeViewport();
                }
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

            TitleBlock.Text = item.Title ?? "";
            
            System.Diagnostics.Debug.WriteLine($"[Spotlight] UpdateUI: {item.Title} | Logo: {item.LogoUrl} | Failed: {item.LogoLoadFailed}");

            if (!string.IsNullOrEmpty(item.LogoUrl) && !item.LogoLoadFailed)
            {
                var logoSource = ImageHelper.GetCachedLogo(item.LogoUrl);
                if (logoSource != null)
                {
                    // [DETECT LOADED PLACEHOLDER] If image is already in cache, check dimensions immediately
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] logoSource Dimensions: {logoSource.PixelWidth}x{logoSource.PixelHeight}");
                    if (logoSource.PixelWidth > 0 && logoSource.PixelWidth < 5 && logoSource.PixelHeight < 5)
                    {
                        item.LogoLoadFailed = true;
                        LogoImage.Visibility = Visibility.Collapsed;
                        TitleBlock.Visibility = Visibility.Visible;
                        System.Diagnostics.Debug.WriteLine($"[Spotlight] Logo source was detected as PLACEHOLDER (size {logoSource.PixelWidth}x{logoSource.PixelHeight}). Falling back to Title.");
                    }
                    else
                    {
                        // [PERF] Only set source if it's actually different to avoid loading loops
                        if (LogoImage.Source != logoSource)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Spotlight] Setting Logo Source (New): {item.LogoUrl}");
                            LogoImage.Source = logoSource;
                        }
                        
                        // [DETECT LOADED PLACEHOLDER] If image is already in cache, check dimensions immediately
                        if (ImageHelper.IsPlaceholder(item.LogoUrl) || (logoSource.PixelWidth > 0 && logoSource.PixelWidth < 10 && logoSource.PixelHeight < 10))
                        {
                            item.LogoLoadFailed = true;
                            LogoImage.Visibility = Visibility.Collapsed;
                            TitleBlock.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            // [PERF] Only set source if it's actually different to avoid loading loops
                            if (LogoImage.Source != logoSource)
                            {
                                LogoImage.Source = logoSource;
                            }
                            
                            // [OPTIMISTIC] Hide title by default if we have a potentially good logo.
                            // The ImageOpened/ImageFailed handlers will switch back to Title if this logo is a dummy.
                            LogoImage.Visibility = Visibility.Visible;
                            TitleBlock.Visibility = Visibility.Collapsed; 
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Logo source was NULL. Falling back to Title.");
                    LogoImage.Visibility = Visibility.Collapsed;
                    TitleBlock.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] UI State: Logo=COLLAPSED, Title=VISIBLE");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] No Logo (or Failed). Showing Title: '{TitleBlock.Text}'");
                LogoImage.Visibility = Visibility.Collapsed;
                TitleBlock.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine($"[Spotlight] UI State: Logo=COLLAPSED, Title=VISIBLE");
            }

            YearBlock.Text = item.Year ?? "";
            
            // [FIX] Format Rating to N1 (e.g., 8.5) and handle 10x multiplier cases (94.0 -> 9.4)
            string rawRating = item.Rating ?? "";
            if (double.TryParse(rawRating.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                if (val > 10) val /= 10.0;
                RatingBlock.Text = val.ToString("N1", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                RatingBlock.Text = rawRating;
            }

            DescriptionBlock.Text = item.Overview ?? item.Meta?.Description ?? "";
            GenresBlock.Text = item.Genres ?? "";

            // Handle visibility of separators if fields are missing
            YearBlock.Visibility = string.IsNullOrEmpty(YearBlock.Text) ? Visibility.Collapsed : Visibility.Visible;
            GenresBlock.Visibility = string.IsNullOrEmpty(GenresBlock.Text) ? Visibility.Collapsed : Visibility.Visible;
            RatingBlock.Visibility = string.IsNullOrEmpty(RatingBlock.Text) ? Visibility.Collapsed : Visibility.Visible;

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

            System.Diagnostics.Debug.WriteLine($"[Spotlight] UI Updated for: {item.Title} (ID: {item.IMDbId}) | Logo: {!string.IsNullOrEmpty(item.LogoUrl)} | Trailer: {!string.IsNullOrEmpty(item.TrailerUrl)} | Desc: {(!string.IsNullOrEmpty(DescriptionBlock.Text) ? "Yes" : "No")} | ImageCandidates: {_currentImageCandidates.Count}");

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
                    if (string.IsNullOrEmpty(e.PropertyName) || 
                        e.PropertyName == nameof(item.Title) || e.PropertyName == nameof(item.Description) || 
                        e.PropertyName == nameof(item.Overview) || e.PropertyName == nameof(item.TrailerUrl) ||
                        e.PropertyName == nameof(item.Rating) || e.PropertyName == nameof(item.Year) || 
                        e.PropertyName == nameof(item.Genres) || e.PropertyName == nameof(item.LogoUrl) ||
                        e.PropertyName == nameof(item.Banner) || e.PropertyName == nameof(item.BackdropUrl))
                    {
                        // Debounce UI update to batch multiple property changes
                        if (_debounceTimer == null)
                        {
                            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                            _debounceTimer.Tick += (s, args) =>
                            {
                                _debounceTimer.Stop();
                                if (_items.Count > 0 && _currentIndex < _items.Count)
                                {
                                    UpdateUI();
                                    SynchronizeViewport(); // [SENIOR] Re-evaluate state now that we have fresh data
                                }
                            };
                        }
                        _debounceTimer.Stop();
                        _debounceTimer.Start();
                    }
                });
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure single subscription
            TrailerPoolService.Instance.TrailerMessageReceived -= Instance_TrailerMessageReceived;
            TrailerPoolService.Instance.TrailerMessageReceived += Instance_TrailerMessageReceived;
            
            UpdateClips();
            SynchronizeViewport();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            StremioMediaStream.OnMetadataUpdated -= HandleGlobalMetadataUpdated;
            CleanupWebView();
        }

        private async Task TryLoadTrailerAsync()
        {
            try
            {
                if (!_isInViewport || _items == null || _currentIndex >= _items.Count) return;

                var currentItem = _items[_currentIndex];
                string rawTrailer = currentItem.TrailerUrl;
                string ytId = TrailerPoolService.ExtractYouTubeId(rawTrailer);

                System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_TRACE] TryLoad: {currentItem.Title} | Raw: {rawTrailer ?? "null"} | Extracted: {ytId ?? "null"} | Owner: {TrailerPoolService.Instance.CurrentContainer == VideoContainer}");

                if (string.IsNullOrEmpty(ytId))
                {
                    System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_TRACE] No valid trailer ID for {currentItem.Title}. Raw: {rawTrailer ?? "null"}");
                    return;
                }

                // [GUARD] Avoid restarting the same trailer
                if (_isTrailerPlaying && _pendingTrailerId == ytId) return;

                _pendingTrailerId = ytId;
                System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_TRACE] Playing: {currentItem.Title} | YT: {ytId}");

                await StartOrSwitchVideoAsync(ytId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_ENGINE] TryLoad Error: {ex.Message}");
            }
        }

        private async Task StartOrSwitchVideoAsync(string ytId)
        {
            if (string.IsNullOrEmpty(ytId)) return;
            
            await _playLock.WaitAsync();
            try 
            {
                System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_TRACE] StartOrSwitch: ID={ytId}, InViewport={_isInViewport}, ActualH={ActualHeight}");
                
                // Allow if in viewport OR if we have a valid layout height (initial sync)
                if (!_isInViewport && ActualHeight <= 0 && _currentIndex != 0) 
                {
                    System.Diagnostics.Debug.WriteLine("[SPOTLIGHT_TRACE] StartOrSwitch aborted: Not in viewport and height is 0.");
                    return;
                }
                
                _pendingTrailerId = ytId;
                _isTrailerPlaying = false; // [STATE RESET] Ensure next READY triggers a fresh reveal

                // [SMART TRANSITION] 
                // Only hide the container if we don't have a WebView yet (Initial Load).
                if (_webView == null && VideoContainer != null) 
                {
                    VideoContainer.Opacity = 0;
                }

                if (_webView == null)
                {
                    _webView = await TrailerPoolService.Instance.AcquireAsync(VideoContainer);
                    if (_webView != null)
                    {
                         await TrailerPoolService.Instance.PlayTrailerAsync(_webView, ytId);
                    }
                }
                else if (_webView.CoreWebView2 != null)
                {
                    await TrailerPoolService.Instance.PlayTrailerAsync(_webView, ytId);
                }
                UpdateMuteButtonIcon();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] StartOrSwitchVideoAsync Error: {ex.Message}");
            }
            finally
            {
                _playLock.Release();
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

                if (FallbackImage.Source is BitmapImage currentBmp && currentBmp.UriSource?.AbsoluteUri == candidate)
                {
                    return; // Same image, don't flicker
                }

                // [OPTIMIZATION] Use SharedImageManager for unified backdrop caching
                var bitmap = SharedImageManager.GetOptimizedImage(candidate, targetWidth: 1280, xamlRoot: this.XamlRoot);
                if (bitmap != null)
                {
                    FallbackImage.Source = bitmap;
                    return;
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
                if (string.IsNullOrWhiteSpace(item.Meta.Originalname) &&
                    !string.IsNullOrWhiteSpace(item.Meta.Name) &&
                    !string.Equals(item.Meta.Name, unified.Title, StringComparison.OrdinalIgnoreCase))
                {
                    item.Meta.Originalname = item.Meta.Name;
                }
            }

            item.UpdateFromUnified(unified);

            if (item == _items[_currentIndex])
            {
                item.OnPropertyChanged(nameof(item.LandscapeImageUrl));
                UpdateUI();
            }
        }


        private void OnTrailerMessageReceived(string msg)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (msg.StartsWith("READY"))
                {
                    string incomingId = msg.Contains(":") ? msg.Split(':')[1] : "";
                    
                    // [SENIOR GUARD] Only reveal if the incoming ID matches our pending trailer
                    if (!string.IsNullOrEmpty(incomingId) && incomingId != _pendingTrailerId)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_VIEW_DEBUG] READY Ignored: ID Mismatch. Expected={_pendingTrailerId}, Got={incomingId}");
                        return;
                    }

                    // [FIX] Race Condition: Check ownership via pool instead of local _webView variable
                    var shared = TrailerPoolService.Instance.SharedWebView;
                    bool isWebViewReady = shared != null && TrailerPoolService.Instance.CurrentContainer == VideoContainer;
                    
                    System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_VIEW_DEBUG] Message: {msg} | WebV: {isWebViewReady} | Container: {VideoContainer != null}");

                    if (!isWebViewReady) 
                    {
                        System.Diagnostics.Debug.WriteLine("[SPOTLIGHT_VIEW_DEBUG] READY Aborted: WebView not attached to this container or owner changed.");
                        return;
                    }

                    if (_isTrailerPlaying) return; // [GUARD] Avoid redundant reveal animations
                    _isTrailerPlaying = true;

                    if (VideoContainer != null)
                    {
                        // Ensure layout is updated before reveal
                        UpdateClips();
                        
                        VideoContainer.Visibility = Visibility.Visible;
                        
                        // [FIX] Ensure the shared WebView itself is opaque and visible
                        if (shared != null)
                        {
                            shared.Opacity = 1;
                            shared.Visibility = Visibility.Visible;
                        }

                        UpdateMuteButtonIcon();

                        // Project Zero: Smooth Fade-In Reveal
                        var anim = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                        Storyboard.SetTarget(anim, VideoContainer);
                        Storyboard.SetTargetProperty(anim, "Opacity");
                        var sb = new Storyboard(); sb.Children.Add(anim); sb.Begin();
                        
                        System.Diagnostics.Debug.WriteLine($"[SPOTLIGHT_VIEW_DEBUG] REVEAL Triggered. Size: {VideoContainer.ActualWidth}x{VideoContainer.ActualHeight}");
                    }
                    if (ExpandButton != null) ExpandButton.Visibility = Visibility.Visible;
                    if (MuteButton != null) MuteButton.Visibility = Visibility.Visible;
                }
                else if (msg == "ERROR")
                {
                    System.Diagnostics.Debug.WriteLine("[SPOTLIGHT_VIEW_DEBUG] Trailer ERROR received.");
                    CleanupWebView();
                }
            });
        }

        private void CleanupWebView()
        {
            _pendingTrailerId = null;
            if (_webView != null)
            {
                TrailerPoolService.Instance.Release(VideoContainer);
                _webView = null;
                _isTrailerPlaying = false;
                
                if (ExpandButton != null) ExpandButton.Visibility = Visibility.Collapsed;
                if (MuteButton != null) MuteButton.Visibility = Visibility.Collapsed;
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
                ItemClicked?.Invoke(this, new SpotlightItemClickedEventArgs(item, FallbackImage, LogoImage.Source));
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
