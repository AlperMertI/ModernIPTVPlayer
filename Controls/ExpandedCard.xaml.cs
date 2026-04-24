using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Windows.UI;
using ModernIPTVPlayer;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services;
using ModernIPTVPlayer.Helpers;
using MpvWinUI;
using System.Globalization;
using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace ModernIPTVPlayer.Controls
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed partial class ExpandedCard : UserControl
    {
        public event EventHandler PlayClicked;
        public event EventHandler<TmdbMovieResult> DetailsClicked;
        public event EventHandler AddListClicked;
        public event EventHandler<bool> CinemaModeToggled;
        
        // Hold data
        private ModernIPTVPlayer.Models.IMediaStream _stream;
        private TmdbMovieResult _tmdbInfo;
        private ModernIPTVPlayer.Models.Metadata.UnifiedMetadata _lastMetadata;
        
        // Cinema Mode State
        private bool _isCinemaMode = false;
        
        // Pre-initialization state
        private bool _isSeriesFinished = false;
        private Microsoft.UI.Composition.Compositor _compositor;

        // Project Zero: Shared Engine Reference
        private WebView2? _webView;

        // Mouse Drag-to-Scroll State
        private bool _isDataDragging = false;
        private Windows.Foundation.Point _lastPointerPos;
        private bool _isCastDragging = false;
        private Windows.Foundation.Point _lastCastPointerPos;
        private string? _currentBackdropUrl;
        private string? _currentLogoUrl;

        public System.Collections.ObjectModel.ObservableCollection<CastItem> CastList { get; private set; } = new();
        public System.Collections.ObjectModel.ObservableCollection<CastItem> DirectorList { get; private set; } = new();
        private bool _isTrailerRevealed = false;

        public Image BannerImage => BackdropImage;

        public ExpandedCard()
        {
            this.InitializeComponent();
            _compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this).Compositor;

            // Robust Drag-to-Scroll Registration (Vertical)
            ContentScrollViewer.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerPressed), true);
            ContentScrollViewer.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerMoved), true);
            ContentScrollViewer.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerReleased), true);
            ContentScrollViewer.AddHandler(PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerReleased), true);
            ContentScrollViewer.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPointerReleased), true);

            // Robust Drag-to-Scroll Registration (Horizontal - Cast List)
            CastListView.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(CastListView_PointerPressed), true);
            CastListView.AddHandler(PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(CastListView_PointerMoved), true);
            CastListView.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(CastListView_PointerReleased), true);
            CastListView.AddHandler(PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(CastListView_PointerReleased), true);
            CastListView.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(CastListView_PointerReleased), true);
            
            // Mouse Wheel Fix: Prevent bubbling to main page
            ContentScrollViewer.PointerWheelChanged += ContentScrollViewer_PointerWheelChanged;

            // Project Zero: Shared event listener
            TrailerPoolService.Instance.TrailerMessageReceived += Instance_TrailerMessageReceived;

            // Project Zero: Mandatory Cleanup Registration
            this.Unloaded += (s, e) => Cleanup();
        }

        public void PrepareForTrailer() 
        {
             // Project Zero: Stub for controller compatibility
             System.Diagnostics.Debug.WriteLine("[ExpandedCard] PrepareForTrailer hook received.");
        }

        private void Instance_TrailerMessageReceived(object? sender, string e)
        {
            // Only process if we are the current owner of the WebView
            if (_webView != null)
            {
                OnTrailerMessageReceived(e);
            }
        }

        /// <summary>
        /// Project Zero: Explicitly releases all resources, breaks interop links, 
        /// and clears large collections to ensure memory is reclaimed by GC/Native.
        /// </summary>
        public void Cleanup()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] ExpandedCard releasing resources for: {_stream?.Title ?? "None"}");
                
                // 1. Release Heavy UI Resources
                BackdropImage.Source = null;
                
                // 2. Kill Trailer Engine (Project Zero: Release shared engine)
                if (_webView != null)
                {
                    TrailerPoolService.Instance.Release(TrailerContainer);
                    _webView = null;
                }

                // 3. Clear Managed Collections (Breaks reference chains)
                CastList?.Clear();
                DirectorList?.Clear();

                // 4. Cancel pending tasks
                TrailerPoolService.Instance.TrailerMessageReceived -= Instance_TrailerMessageReceived;

                // 5. Break Interop Facades
                _compositor = null;
                _stream = null;
                _tmdbInfo = null;

                // 6. Unsubscribe from manual handlers 
                ContentScrollViewer.PointerWheelChanged -= ContentScrollViewer_PointerWheelChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Cleanup] Error during ExpandedCard release: {ex.Message}");
            }
        }

        #region Drag-to-Scroll Logic (Vertical)
        private void OnPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Conflict Resolution: If we are already dragging the horizontal list, ignore vertical start
            if (_isCastDragging) return;

            var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isDataDragging = true;
                _lastPointerPos = ptr.Position;
                ContentScrollViewer.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDataDragging)
            {
                // Conflict Resolution: If horizontal dragging started somehow, stop vertical
                if (_isCastDragging)
                {
                    _isDataDragging = false;
                    ContentScrollViewer.ReleasePointerCapture(e.Pointer);
                    return;
                }

                var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
                
                // Safety: check if left button is still pressed
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    _isDataDragging = false;
                    ContentScrollViewer.ReleasePointerCapture(e.Pointer);
                    return;
                }

                double deltaY = _lastPointerPos.Y - ptr.Position.Y;
                if (Math.Abs(deltaY) > 0.1)
                {
                    ContentScrollViewer.ChangeView(null, ContentScrollViewer.VerticalOffset + deltaY, null, true);
                    _lastPointerPos = ptr.Position;
                    e.Handled = true;
                }
            }
        }

        private void OnPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDataDragging)
            {
                _isDataDragging = false;
                ContentScrollViewer.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }
        public void OnRootPointerWheelChanged(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            ContentScrollViewer_PointerWheelChanged(ContentScrollViewer, e);
        }

        private void RootGrid_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            OnRootPointerWheelChanged(e);
        }

        private void ContentScrollViewer_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var prop = e.GetCurrentPoint(ContentScrollViewer).Properties;
            double delta = prop.MouseWheelDelta;
            if (delta != 0)
            {
                // Manual scroll to ensure focus doesn't matter, and mark as handled
                ContentScrollViewer.ChangeView(null, ContentScrollViewer.VerticalOffset - (delta), null, true);
                e.Handled = true;
            }
        }
        #endregion

        #region Drag-to-Scroll Logic (Horizontal - Cast List)
        private void CastListView_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && ptr.Properties.IsLeftButtonPressed)
            {
                _isCastDragging = true;
                _lastCastPointerPos = ptr.Position;
                CastListView.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void CastListView_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isCastDragging)
            {
                var ptr = e.GetCurrentPoint(null); // Use window coords for smoothness

                // Safety: check if left button is still pressed
                if (!ptr.Properties.IsLeftButtonPressed)
                {
                    _isCastDragging = false;
                    CastListView.ReleasePointerCapture(e.Pointer);
                    return;
                }

                double deltaX = _lastCastPointerPos.X - ptr.Position.X;
                if (Math.Abs(deltaX) > 0.1)
                {
                    var sv = GetScrollViewer(CastListView);
                    if (sv != null)
                    {
                        sv.ChangeView(sv.HorizontalOffset + deltaX, null, null, true);
                    }
                    _lastCastPointerPos = ptr.Position;
                    e.Handled = true;
                }
            }
        }

        private void CastListView_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isCastDragging)
            {
                _isCastDragging = false;
                CastListView.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        private ScrollViewer GetScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer) return depObj as ScrollViewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
        #endregion
        
        /// <summary>
        /// Public method to stop trailer playback when card is hidden
        /// </summary>
        public async Task StopTrailer(bool forceDestroy = false)
        {
            try
            {
                if (_webView != null)
                {
                    TrailerPoolService.Instance.Release(TrailerContainer);
                    _webView = null;
                }
                _isTrailerRevealed = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error in StopTrailer: {ex.Message}");
            }

            if (_isCinemaMode) ToggleCinemaMode(false);
            ResetState(isMorphing: false, isStopping: true);
        }
        private void OnTrailerMessageReceived(string message)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                try
                {
                    if (message == "READY")
                    {
                       RevealTrailerInternal();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] WebMessage Error: {ex.Message}");
                }
            });
        }

        private long _loadNonce = 0;

        private async void FavButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stream == null) return;

            var manager = Services.WatchlistManager.Instance;
            bool isInList = manager.IsOnWatchlist(_stream);

            if (isInList)
            {
                await manager.RemoveFromWatchlist(_stream);
            }
            else
            {
                await manager.AddToWatchlist(_stream);
            }

            // Animate Icon
            UpdateWatchlistIcon(!isInList, animate: true);
        }

        private void UpdateWatchlistIcon(bool isAdded, bool animate = false)
        {
            var icon = (FontIcon)FavButton.Content;
            string newGlyph = isAdded ? "\uE73E" : "\uE710"; // Checkmark vs Plus

            if (animate)
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
                var compositor = visual.Compositor;

                var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(1f, 1f, 1f));
                scaleAnim.InsertKeyFrame(0.5f, new System.Numerics.Vector3(1.4f, 1.4f, 1f));
                scaleAnim.InsertKeyFrame(1f, new System.Numerics.Vector3(1f, 1f, 1f));
                scaleAnim.Duration = TimeSpan.FromMilliseconds(300);
                scaleAnim.Target = "Scale";

                visual.StartAnimation("Scale", scaleAnim);
            }

            icon.Glyph = newGlyph;
            FavButton.Background = isAdded 
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 33, 150, 243)) // Blue when added
                : new SolidColorBrush(Windows.UI.Color.FromArgb(34, 255, 255, 255)); // Transparent white
        }

        public void ToggleCinemaMode(bool enable)
        {
            if (_isCinemaMode == enable) return;
            _isCinemaMode = enable;

            if (enable)
            {
                // Enter Cinema Mode
                // 1. Hide Content Rows
                RootGrid.RowDefinitions[1].Height = new GridLength(0);
                RootGrid.RowDefinitions[2].Height = new GridLength(0);
                
                // 2. Maximize Trailer Row
                RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                
                // 3. Unmute if muted
                if (_isMuted)
                {
                    _ = SetMutedAsync(false);
                }
                
                // 4. Change Icon to Shrink
                ((FontIcon)ExpandButton.Content).Glyph = "\uE73F"; // Shrink Icon
            }
            else
            {
                // Exit Cinema Mode
                // 1. Restore Rows
                RootGrid.RowDefinitions[0].Height = new GridLength(160);
                RootGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                RootGrid.RowDefinitions[2].Height = GridLength.Auto;

                // 2. Change Icon to Expand
                ((FontIcon)ExpandButton.Content).Glyph = "\uE740"; // Expand Icon
            }

            CinemaModeToggled?.Invoke(this, _isCinemaMode);
        }

        private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleCinemaMode(!_isCinemaMode);

        /// <summary>
        /// Resets the card to initial state before loading new data
        /// </summary>
        private void ResetState(bool isMorphing = false, bool isStopping = false, bool forceSkeleton = true, long sessionNonce = -1)
        {
            // Transactional Guard: If a reset arrives from an old session (e.g. a late StopTrailer), ignore it.
            if (sessionNonce != -1 && sessionNonce != _loadNonce)
            {
                System.Diagnostics.Debug.WriteLine($"[EXP-RESET] REJECTED | Target Session: {sessionNonce} | Current: {_loadNonce}");
                return;
            }

            // 1. STRATEGY: Calculate our behavior once at the top
            bool isSmartSwap = !forceSkeleton;
            bool shouldProtectIdentity = isSmartSwap || isStopping;
            System.Diagnostics.Debug.WriteLine($"[EXP-RESET] Start | Session: {sessionNonce} | SmartSwap: {isSmartSwap} | Stopping: {isStopping} | IdentityProtected: {shouldProtectIdentity} | ForceSkeleton: {forceSkeleton}");

            // 2. STATE MANAGEMENT: Reset memory only if we are truly moving to a NEW item
            if (!isStopping) 
            {
                _lastMetadata = null;
                // [FIX] Preserve revealed state during smart swaps to prevent redundant stagger animations
                if (!isSmartSwap)
                {
                    _isRevealed = false;
                }
            }
            
            // 3. UNIVERSAL CLEANUP: Reset layout, trailers, and progress
            ResetUniversalUi(shouldProtectIdentity);

            // 4. TRANSITION LOGIC: Manage Skeletons and Opacity
            ResetTransitionLayer(isMorphing, isStopping, forceSkeleton, isSmartSwap);

            // 5. CONTENT WIPING:
            WipeExpandedContent(shouldProtectIdentity);

            if (isStopping)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetUniversalUi(bool shouldProtectDynamicData = false)
        {
            // Layout & Alignment
            if (!shouldProtectDynamicData)
            {
                LogoContainer.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                TitleText.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine("[EXP-LAYOUT] 0ms Seed - Title Visible forced");
                TitleText.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                TitleText.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
                GenresText.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                GenresText.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
            }
            MetadataLine.Visibility = Visibility.Visible;
            WritersText.Visibility = Visibility.Collapsed;
            BackdropContainer.Visibility = Visibility.Visible;
            
            // [FIX] Reset trailer revelation state so AmbienceGrid is hidden for the new trailer
            _isTrailerRevealed = false;
            AmbienceGrid.Visibility = Visibility.Visible;

            // Media & Trailers
            if (_webView != null)
            {
                TrailerPoolService.Instance.Release(TrailerContainer);
                _webView = null;
            }
            
            TrailerContainer.Visibility = Visibility.Collapsed;
            MuteButton.Visibility = Visibility.Collapsed;
            ExpandButton.Visibility = Visibility.Collapsed;
            _isMuted = true;
            UpdateMuteIcon();

            // Playback & Badges
            if (!shouldProtectDynamicData)
            {
                PlayButtonSubtext.Visibility = Visibility.Collapsed;
                PlayButtonSubtext.Text = "";
                PlaybackProgressBar.Visibility = Visibility.Collapsed;
                ProgressPanel.Visibility = Visibility.Collapsed;
                TimeLeftText.Text = "";
                TechBadgesPanel.Children.Clear();
                TechBadgesPanel.Visibility = Visibility.Collapsed;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                StaticMetadataPanel.Visibility = Visibility.Collapsed;
            }
            MoodTag.Visibility = Visibility.Collapsed;
        }

        private void ResetTransitionLayer(bool isMorphing, bool isStopping, bool forceSkeleton, bool isSmartSwap)
        {
            if (isStopping) return;

            BackdropImage.Opacity = 0.7;
            BackdropOverlay.Visibility = Visibility.Visible;

            if (forceSkeleton)
            {
                if (isMorphing)
                {
                    // Smooth Crossfade to Skeleton
                    var vContent = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RealContentPanel);
                    var vSkeleton = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MainSkeleton);
                    MainSkeleton.Visibility = Visibility.Visible;
                    vSkeleton.Opacity = 0f;

                    try
                    {
                        if (_compositor == null)
                        {
                            RealContentPanel.Opacity = 0;
                            MainSkeleton.Visibility = Visibility.Visible;
                            MainSkeleton.Opacity = 1;
                        }
                        else
                        {
                            var animOut = _compositor.CreateScalarKeyFrameAnimation();
                            animOut.InsertKeyFrame(1.0f, 0f);
                            animOut.Duration = TimeSpan.FromMilliseconds(200);
                            animOut.Target = "Opacity";

                            var animIn = _compositor.CreateScalarKeyFrameAnimation();
                            animIn.InsertKeyFrame(1.0f, 1f);
                            animIn.Duration = TimeSpan.FromMilliseconds(200);
                            animIn.Target = "Opacity";

                            vContent.StartAnimation("Opacity", animOut);
                            vSkeleton.StartAnimation("Opacity", animIn);
                            MainSkeleton.Visibility = Visibility.Visible;
                            MainSkeleton.Opacity = 1;
                        }
                    }
                    catch 
                    { 
                        RealContentPanel.Opacity = 0; 
                        MainSkeleton.Visibility = Visibility.Visible; 
                    }
                }
                else
                {
                    // Instant Skeleton
                    RealContentPanel.Opacity = 0;
                    MainSkeleton.Visibility = Visibility.Visible;
                    MainSkeleton.Opacity = 1;
                    BadgeSkeleton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                // Smart Swap: Keep content visible for instant swap
                RealContentPanel.Opacity = 1;
                MainSkeleton.Visibility = Visibility.Collapsed;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                var vPanel = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(RealContentPanel);
                vPanel.Opacity = 1f;
            }
        }

        private void WipeExpandedContent(bool shouldProtectIdentity)
        {
            if (shouldProtectIdentity)
            {
                System.Diagnostics.Debug.WriteLine($"[EXP-VERBOSE] UI SHIELDED - Skipping Wipe | Current Title: {TitleText.Text}");
                return;
            }

            // FULL WIPE: Only happens when moving to a brand new item with a cache miss
            System.Diagnostics.Debug.WriteLine($"[EXP-VERBOSE] Identity WIPE active (Full Clear) | Nonce: {_loadNonce}");
            
            // Layer A (Identity)
            TitleText.Text = "";
            LogoContainer.Visibility = Visibility.Collapsed;
            LogoImage.Source = null;
            BackdropImage.Source = null;
            _currentBackdropUrl = null;
            _currentLogoUrl = null;
            GenresText.Text = "";
            YearText.Text = "";
            RatingText.Text = "";

            // Layer B (Details)
            DescText.Text = "";
            WritersText.Text = "";
            RatingText.Visibility = Visibility.Collapsed;
            YearText.Visibility = Visibility.Collapsed;
            
            if (BadgeAge != null) { BadgeAge.Visibility = Visibility.Collapsed; if (BadgeAgeText != null) BadgeAgeText.Text = ""; }
            if (BadgeCountry != null) { BadgeCountry.Visibility = Visibility.Collapsed; if (BadgeCountryText != null) BadgeCountryText.Text = ""; }
            
            CastList.Clear();
            CastHeaderText.Visibility = Visibility.Collapsed;
            CastListView.Visibility = Visibility.Collapsed;
            DirectorList.Clear();
            DirectorHeaderText.Visibility = Visibility.Collapsed;
            DirectorListView.Visibility = Visibility.Collapsed;

            // LAYER C: Feedback - Only show spinner if we don't have cached data yet
            AmbienceGrid.Visibility = (shouldProtectIdentity) ? Visibility.Collapsed : Visibility.Visible;
            LoadingRing.IsActive = !shouldProtectIdentity;
            LoadingRing.Visibility = (shouldProtectIdentity) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Fire-and-forget initialization for background services.
        /// These are already initialized at app startup or in other pages,
        /// so we don't block the UI waiting for them.
        /// </summary>
        private async Task InitializeServicesAsync()
        {
            try
            {
                var histTask = HistoryManager.Instance.InitializeAsync();
                var histTimeout = Task.Delay(2000);
                await Task.WhenAny(histTask, histTimeout);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] HistoryManager init error: {ex.Message}");
            }

            try
            {
                var watchTask = Services.WatchlistManager.Instance.InitializeAsync();
                var watchTimeout = Task.Delay(2000);
                await Task.WhenAny(watchTask, watchTimeout);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] WatchlistManager init error: {ex.Message}");
            }
        }

        public async Task LoadDataAsync(ModernIPTVPlayer.Models.IMediaStream stream, bool isMorphing = false)
        {
            _isSeriesFinished = false; // Reset on new load
            // 1. Transactional Update
            _loadNonce++;
            long loadNonce = _loadNonce;

            // 2. High-Fidelity Seeding (Architectural Improvement)
            // Immediately extract all available local data from the stream.
            var seed = ModernIPTVPlayer.Models.Metadata.UnifiedMetadata.FromStream(stream);
            bool hasRealCache = Services.Metadata.MetadataProvider.Instance.TryPeekMetadata(stream, Models.Metadata.MetadataContext.ExpandedCard) != null;
            
            // Engineering Decision: If we have seed data, we enable "Smart Swap" 
            // even if the item isn't in the global enriched cache yet.
            bool isEligibleForSmartSwap = isMorphing || seed != null || hasRealCache;

            System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Start Session {loadNonce}: {stream.Title} | SmartSwap: {isEligibleForSmartSwap}");

            _stream = stream;

            // 3. Authority Seeding (0ms)
            // [FIX] Perform seed update AFTER setting the stream to ensure all UI components have context
            if (seed != null)
            {
                UpdateUiFromUnified(seed);
            }

            // 4. Session-Locked Reset (Shielded if smart swapping)
            ResetState(isMorphing, forceSkeleton: !isEligibleForSmartSwap, sessionNonce: loadNonce);

            // [FIX] If we are smart-swapping with seed data, ensure we don't re-trigger the reveal stagger
            if (seed != null && isEligibleForSmartSwap)
            {
                _isRevealed = true; 
            }

            // Minimal delay for layout settlement, then start enrichment tasks
            await Task.Delay(10);
            System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Post-delay checkpoint. loadNonce matching: {loadNonce == _loadNonce}");
            if (loadNonce != _loadNonce) return; 
            
            System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Starting Metadata request via MetadataProvider...");
            // Initial Badges (Static parse before probe/metadata)
            UpdateTechnicalBadges(stream);

            // ... (Rest of UI updates follow seamlessly using the seed data) ...

            // Stremio Badge Logic: Hide technical skeleton if no history available
            if (stream is StremioMediaStream stremio)
            {
                var history = HistoryManager.Instance.GetProgress(stremio.IMDbId);
                if (history == null || string.IsNullOrEmpty(history.StreamUrl))
                {
                    // No history or URL yet - hide tech skeleton and panel
                    BadgeSkeleton.Visibility = Visibility.Collapsed;
                    TechBadgesPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TechBadgesPanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                TechBadgesPanel.Visibility = Visibility.Visible;
            }

            UpdatePlayButton(stream);
            UpdateProgressState(stream);

            // Check Watchlist State (non-blocking - IsOnWatchlist returns false if not initialized yet)
            UpdateWatchlistIcon(Services.WatchlistManager.Instance.IsOnWatchlist(stream), animate: false);

            try
            {
                var metadataTask = Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(stream, Models.Metadata.MetadataContext.ExpandedCard, onUpdate: (partial) => 
                {
                    this.DispatcherQueue.TryEnqueue(() => 
                    {
                        if (loadNonce != _loadNonce) 
                        {
                            System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] onUpdate IGNORED (Stale Nonce): {partial.Title} | TaskNonce: {loadNonce} | Current: {_loadNonce}");
                            return;
                        }
                        
                        lock (partial.SyncRoot)
                        {
                             System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] onUpdate Dispatching: {partial.Title} | DataSource: {partial.DataSource}");
                             UpdateUiFromUnified(partial);
                        }
                    });
                });
                var timeoutTask = Task.Delay(15000);
                var completedTask = await Task.WhenAny(metadataTask, timeoutTask);
                System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Wait complete. metadataTask status: {metadataTask.Status} | Timing out: {completedTask == timeoutTask}");
                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] TIMEOUT HIT for metadata.");
                    DescText.Text = "Metadata loading timed out.";
                    MainSkeleton.Visibility = Visibility.Collapsed;
                    RealContentPanel.Opacity = 1;
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    BackdropImage.Opacity = 1.0;
                    BackdropOverlay.Visibility = Visibility.Collapsed;
                    return;
                }

                var unified = await metadataTask;
                System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Metadata task returned. unified: {unified?.Title}");
                if (loadNonce != _loadNonce) 
                {
                    System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] STALE NONCE after await metadataTask. Aborting.");
                    return;
                }

                if (unified != null)
                {
                    _tmdbInfo = unified.TmdbInfo; // Keep for DetailsClicked compatibility

                    string displayTitle = unified.Title;
                    string displaySubtitle = unified.Genres;
                    string displayOverview = unified.Overview;
                    string displayBackdrop = unified.BackdropUrl;

                    // --- EPISODE RESUME LOGIC (Enhanced with UnifiedMetadata) ---
                    if (unified.IsSeries)
                    {
                        var history = HistoryManager.Instance.GetLastWatchedEpisode(unified.MetadataId);
                        ModernIPTVPlayer.Models.Metadata.UnifiedEpisode nextEp = null;
                        
                        if (history != null && unified.Seasons != null)
                        {
                            // 1. Locate the season for the history item
                            var historySeason = unified.Seasons.FirstOrDefault(s => s.SeasonNumber == history.SeasonNumber);
                            if (historySeason != null)
                            {
                                var historyEp = historySeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == history.EpisodeNumber);
                                
                                // 2. Check if we should show Next Episode
                                if (history.IsFinished)
                                {
                                    // Try next in same season
                                    nextEp = historySeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == history.EpisodeNumber + 1);
                                    
                                    // If not found, try first of next season
                                    if (nextEp == null)
                                    {
                                        var nextSeason = unified.Seasons.FirstOrDefault(s => s.SeasonNumber == history.SeasonNumber + 1);
                                        if (nextSeason != null)
                                        {
                                            nextEp = nextSeason.Episodes.FirstOrDefault(e => e.EpisodeNumber == 1);
                                        }
                                    }
                                    
                                    // If still null, maybe stick to history display? Or just show the last one but marked watched?
                                    // Logic: if no next episode, likely end of series. Show last watched.
                                    if (nextEp == null && historyEp != null)
                                    {
                                         nextEp = historyEp;
                                         _isSeriesFinished = true; // Mark as finished for UI
                                    }
                                }
                                else
                                {
                                    // Not finished -> Show this episode (Resume)
                                    nextEp = historyEp;
                                }
                            }
                        }

                        // 3. Fallback: If no history, try S1E1 ONLY if user has already started watching (not for unwatched series)
                        // For unwatched series, show series-level info instead of episode info
                        if (nextEp == null && history != null && history.IsFinished && unified.Seasons != null)
                        {
                            // User has started watching and finished - find next episode
                            var s1 = unified.Seasons.FirstOrDefault(s => s.SeasonNumber == 1) ?? unified.Seasons.FirstOrDefault();
                            if (s1 != null)
                            {
                                nextEp = s1.Episodes.FirstOrDefault(e => e.EpisodeNumber == 1) ?? s1.Episodes.FirstOrDefault();
                            }
                        }

                        // 4. Update Display
                        if (nextEp != null)
                        {
                            string epName = nextEp.Title;
                            // Clean title logic (borrowed from existing)
                            bool isGeneric = string.IsNullOrEmpty(epName) || 
                                            epName.Contains("Bölüm", StringComparison.OrdinalIgnoreCase) || 
                                            epName.Contains("Episode", StringComparison.OrdinalIgnoreCase) || 
                                            epName == nextEp.EpisodeNumber.ToString();

                            // Use history title if available and we are showing the SAME episode? 
                            // No, relying on Unified Metadata is better for "Next Episode".
                            
                            if (isGeneric)
                                displayTitle = $"S{nextEp.SeasonNumber:D2}E{nextEp.EpisodeNumber:D2}";
                            else
                                displayTitle = $"S{nextEp.SeasonNumber:D2}E{nextEp.EpisodeNumber:D2} - {epName}";

                            if (!string.IsNullOrEmpty(nextEp.Overview)) displayOverview = nextEp.Overview;
                            if (!string.IsNullOrEmpty(nextEp.ThumbnailUrl)) displayBackdrop = nextEp.ThumbnailUrl;
                        }
                    }

                    // Update UI IMMEDIATELY
                    System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Authoritative Result Arrived for: {unified.Title}");
                    UpdateUiFromUnified(unified, displayTitle, displaySubtitle, displayOverview, displayBackdrop);

                    // NOW Fetch Trailer (Provider might have pre-filled this from TMDB if enabled)
                    string trailerKey = unified.TrailerUrl;

                    if (string.IsNullOrEmpty(trailerKey) && unified.TmdbInfo != null)
                    {
                         trailerKey = await TmdbHelper.GetTrailerKeyAsync(unified.TmdbInfo.Id, unified.IsSeries);
                         if (!string.IsNullOrEmpty(trailerKey))
                         {
                             unified.TrailerUrl = trailerKey;
                         }
                    }

                    if (loadNonce != _loadNonce) return;

                    // [CONSOLIDATION] Synchronize the underlying stream with high-quality metadata 
                    // (Done after trailer potential update for completeness)
                    if (stream != null)
                    {
                        try { stream.UpdateFromUnified(unified); }
                        catch (Exception syncEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ExpandedCard] UpdateFromUnified skipped: {syncEx.Message}");
                        }
                    }

                    if (!string.IsNullOrEmpty(trailerKey))
                    {
                         System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Triggering PlayTrailer: {trailerKey}");
                         await PlayTrailer(videoKey: trailerKey);
                    }
                    else
                    {
                        LoadingRing.IsActive = false;
                        LoadingRing.Visibility = Visibility.Collapsed;
                        BackdropImage.Opacity = 1.0;
                        BackdropOverlay.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    DescText.Text = "No additional details found.";
                    MainSkeleton.Visibility = Visibility.Collapsed;
                    RealContentPanel.Opacity = 1;
                    YearText.Visibility = Visibility.Collapsed;
                    RatingText.Visibility = Visibility.Collapsed;
                    
                    LoadingRing.IsActive = false; 
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
                
                // Run Probe in Background - Do NOT await it to block the UI interaction
                // BadgeSkeleton remains Visible until this finishes
                _ = ProbeStreamInternal(stream, loadNonce);

            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[EXP-LOAD] Session {loadNonce} CANCELLED.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error: {ex.Message}");
                DescText.Text = "Error loading details.";
                MainSkeleton.Visibility = Visibility.Collapsed;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                RealContentPanel.Opacity = 1;
            }
            finally
            {
                if (loadNonce == _loadNonce)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    
                    // [SAFETY NET] Ensure all skeletons are collapsed and content is revealed if we are still active
                    if (MainSkeleton.Visibility == Visibility.Visible || BadgeSkeleton.Visibility == Visibility.Visible)
                    {
                        System.Diagnostics.Debug.WriteLine("[EXP-LOAD] Safety Net Triggered - Forcing Skeleton Collapse");
                        MainSkeleton.Visibility = Visibility.Collapsed;
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                        RealContentPanel.Opacity = 1;
                    }
                }
            }
        }
        
        private async Task ProbeStreamInternal(IMediaStream stream, long loadNonce)
        {
            if (stream == null) return;
            
            string url = null;
            if (stream is LiveStream live)
            {
                if (live.HasMetadata || live.IsProbing) return;
                url = live.StreamUrl;
            }
            else if (stream is SeriesStream series)
            {
                if (series.HasMetadata || series.IsProbing) return;
                // For series, probe the last watched episode if any
                var history = HistoryManager.Instance.GetLastWatchedEpisode(series.SeriesId.ToString());
                if (history != null) 
                {
                    if (!history.IsFinished)
                    {
                        url = history.StreamUrl;
                    }
                }
                else if (App.CurrentLogin != null)
                {
                    try
                    {
                        var info = await Services.ContentCacheService.Instance.GetSeriesInfoAsync(series.SeriesId, App.CurrentLogin);
                        if (info != null && info.Episodes != null && info.Episodes.Count > 0)
                        {
                             // Find First Season
                             var firstSeasonKey = info.Episodes.Keys.OrderBy(k => 
                             {
                                 if (int.TryParse(k, out int s)) return s;
                                 return 9999;
                             }).FirstOrDefault();
                             
                             if (firstSeasonKey != null && info.Episodes.TryGetValue(firstSeasonKey, out var eps) && eps != null)
                             {
                                 var firstEp = eps.OrderBy(e => 
                                 {
                                     if (int.TryParse(e.EpisodeNum?.ToString(), out int en)) return en;
                                     return 9999;
                                 }).FirstOrDefault();
                                 
                                 if (firstEp != null)
                                 {
                                     // Construct URL: /series/{user}/{pass}/{id}.{ext}
                                     var host = App.CurrentLogin.Host.TrimEnd('/');
                                     url = $"{host}/series/{App.CurrentLogin.Username}/{App.CurrentLogin.Password}/{firstEp.Id}.{firstEp.ContainerExtension}";
                                 }
                             }
                        }
                    }
                    catch (Exception ex)
                    {
                        Services.CacheLogger.Error(Services.CacheLogger.Category.Probe, "Failed fetching Series Info", ex.Message);
                    }
                }
            }
            else if (stream is StremioMediaStream stremioItem)
            {
                HistoryItem? history = null;
                if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                    history = HistoryManager.Instance.GetLastWatchedEpisode(stremioItem.IMDbId);
                else
                    history = HistoryManager.Instance.GetProgress(stremioItem.IMDbId);

                if (history != null)
                {
                    if (stremioItem.Meta.Type == "series" || stremioItem.Meta.Type == "tv")
                    {
                         if (!history.IsFinished) url = history.StreamUrl;
                    }
                    else
                    {
                        url = history.StreamUrl;
                    }
                }
            }
            else if (stream is WatchlistItem w)
            {
                if (w.Type == "series")
                {
                    var history = HistoryManager.Instance.GetLastWatchedEpisode(w.Id);
                    if (history != null && !history.IsFinished) url = history.StreamUrl;
                }
                else
                {
                    var history = HistoryManager.Instance.GetProgress(w.Id);
                    if (history != null) url = history.StreamUrl;
                    else if (!string.IsNullOrEmpty(w.StreamUrl)) url = w.StreamUrl;
                }
            }

            if (string.IsNullOrEmpty(url)) 
            {
                DispatcherQueue.TryEnqueue(() => BadgeSkeleton.Visibility = Visibility.Collapsed);
                return;
            }

            // 0. CHECK IPTV METADATA FIRST (USER REQUEST) - Avoid Probing if possible
            if (stream != null && (!string.IsNullOrEmpty(stream.Resolution) || !string.IsNullOrEmpty(stream.Codec)))
            {
                Services.CacheLogger.Info(Services.CacheLogger.Category.Probe, "IPTV METADATA: Skipping probe, using provided info (ExpandedCard)", url);
                var data = new Services.ProbeData
                {
                    Resolution = stream.Resolution,
                    Codec = stream.Codec,
                    Bitrate = stream.Bitrate,
                    IsHdr = stream.IsHdr
                };
                DispatcherQueue.TryEnqueue(() => {
                    BadgeSkeleton.Visibility = Visibility.Collapsed;
                    ApplyProbeResult(stream, data, loadNonce);
                });
                return;
            }

            try
            {
                // 1. Check ID-Based Cache (v2.4)
                if (Services.ProbeCacheService.Instance.Get(stream.Id) is Services.ProbeData cached)
                {
                    Services.CacheLogger.Success(Services.CacheLogger.Category.Probe, "ExpandedCard Cache Hit", url);
                    if (loadNonce == _loadNonce)
                    {
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                        ApplyProbeResult(stream, cached, loadNonce);
                    }
                    return;
                }

                // 2. URL changed check: If this specific stream object already has metadata 
                // but the URL we are probing is DIFFERENT, it means the source changed.
                // We should clear old data if the stream object is the same but URL is new.
                // Actually, ProbeData is URL-keyed, so if URL is new, it's already a 'miss'.
                // The task is to ensure we DON'T use old data if URL changed.
                // Since Get(url) returned null, we are good. 

                // 3. Probe Network (v2.4: ID-based)
                SetProbing(stream, true);
                Services.CacheLogger.Info(Services.CacheLogger.Category.Probe, "Probing Network (ExpandedCard - libmpv)", url);
                var result = await Services.StreamProberService.Instance.ProbeAsync(stream.Id, url);
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Probe COMPLETED for {url}. Success: {result.Success}, Res: {result.Resolution}");
                
                if (result.Success)
                {
                    // Direct apply (already saved to cache by service)
                    var data = new Services.ProbeData 
                    { 
                        Resolution = result.Resolution, 
                        Fps = result.Fps, 
                        Codec = result.Codec, 
                        Bitrate = result.Bitrate, 
                        IsHdr = result.IsHdr 
                    };
                    ApplyProbeResult(stream, data, loadNonce);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Probe FAILED (Success=false) for {url}");
                    Services.CacheLogger.Warning(Services.CacheLogger.Category.Probe, "Probing Failed (Results empty)", url);
                }
            }
            catch (Exception ex)
            {
                Services.CacheLogger.Error(Services.CacheLogger.Category.Probe, "ExpandedCard Probe Error", ex.Message);
            }
            finally
            {
                SetProbing(stream, false);
            }
        }

        private void SetProbing(IMediaStream stream, bool isProbing)
        {
            stream.IsProbing = isProbing;
            
            DispatcherQueue.TryEnqueue(() =>
            {
                if (isProbing)
                {
                    BadgeSkeleton.Visibility = Visibility.Visible;
                    TechBadgesPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Always ensure skeleton is hidden when probing stops
                    if (BadgeSkeleton.Visibility == Visibility.Visible)
                        BadgeSkeleton.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void ApplyProbeResult(IMediaStream stream, Services.ProbeData result, long loadNonce)
        {
            if (loadNonce != _loadNonce) return;

            stream.Resolution = result.Resolution;
            stream.Fps = result.Fps;
            stream.Codec = result.Codec;
            stream.Bitrate = result.Bitrate;
            stream.IsOnline = true;
            stream.IsHdr = result.IsHdr;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (loadNonce != _loadNonce) return;
                BadgeSkeleton.Visibility = Visibility.Collapsed;
                UpdateTechnicalBadges(stream);
                UpdatePlayButton(stream);
                UpdateProgressState(stream);
            });
        }

        private void UpdateTechnicalBadges(IMediaStream stream)
        {
            if (stream == null) return;
            
            TechBadgesPanel.Children.Clear();
            
            // Per User: "Don't show any tech badges if we don't know the source url"
            // We check for Metadata presence as a proxy for 'probed/sourced' data.
            if (!stream.HasMetadata)
            {
                TechBadgesPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // 1. RESOLUTION
            string res = stream.Resolution;
            if (!string.IsNullOrEmpty(res))
            {
                bool is4K = res.Contains("3840") || res.Contains("4096") || res.Contains("4K");
                if (is4K) AddBadge("4K UHD", Colors.Purple);
                else AddBadge(res, Colors.Teal);
            }

            // 2. CODEC
            if (!string.IsNullOrEmpty(stream.Codec))
            {
                AddBadge(stream.Codec, Colors.Orange);
            }

            // 3. HDR / SDR
            if (stream.IsHdr) AddBadge("HDR", Colors.Gold);
            else AddBadge("SDR", Colors.DimGray);
            
            // 4. BITRATE
            if (stream.Bitrate > 0)
            {
                double mbpsValue = stream.Bitrate / 1000000.0;
                string formattedStr = mbpsValue >= 1.0 ? $"{mbpsValue:F1} Mbps" : $"{stream.Bitrate / 1000} kbps";
                AddBadge(formattedStr, Colors.Orange);
            }

            TechBadgesPanel.Visibility = TechBadgesPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddBadge(string text, Color color)
        {
             var border = new Border
             {
                 CornerRadius = new CornerRadius(3),
                 Padding = new Thickness(4, 1, 4, 1),
                 Background = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
                 BorderBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)),
                 BorderThickness = new Thickness(1),
                 VerticalAlignment = VerticalAlignment.Center
             };
             
             var tb = new TextBlock
             {
                 Text = text,
                 FontSize = 10,
                 FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                 Foreground = new SolidColorBrush(Colors.White)
             };
             
             border.Child = tb;
             TechBadgesPanel.Children.Add(border);
        }

        private async Task PlayTrailer(string videoKey)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] PlayTrailer Requested via Pool: {videoKey}");
                _webView = await TrailerPoolService.Instance.AcquireAsync(TrailerContainer, videoKey);
                
                // Mute state setup
                _isMuted = true;
                UpdateMuteIcon();
                if (_webView != null)
                    _ = RefreshMuteStateFromPlayerAsync(defaultMutedWhenUnknown: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] PlayTrailer Error: {ex.Message}");
            }
        }
        
        private void RevealTrailerInternal()
        {
            if (_isTrailerRevealed) return;
            _isTrailerRevealed = true;

            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            BackdropContainer.Visibility = Visibility.Collapsed;
            AmbienceGrid.Visibility = Visibility.Collapsed;

            TrailerContainer.Visibility = Visibility.Visible;
            if (_webView != null) _webView.Opacity = 1;

            MuteButton.Visibility = Visibility.Visible;
            ExpandButton.Visibility = Visibility.Visible;
        }

        private bool _isRevealed;
        private bool _isMuted = true;

        private async Task RefreshMuteStateFromPlayerAsync(bool defaultMutedWhenUnknown = true)
        {
            try
            {
                if (_webView?.CoreWebView2 == null)
                {
                    _isMuted = defaultMutedWhenUnknown;
                    UpdateMuteIcon();
                    return;
                }

                // Check YouTube-specific muted state via simple script (matching engine implementation)
                var raw = await _webView.CoreWebView2.ExecuteScriptAsync("try { player.isMuted(); } catch(e) { true; }");
                _isMuted = raw.Trim().ToLower() == "true";
                UpdateMuteIcon();
            }
            catch
            {
                _isMuted = defaultMutedWhenUnknown;
                UpdateMuteIcon();
            }
        }

        private async Task SetMutedAsync(bool shouldMute)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;
                await _webView.CoreWebView2.ExecuteScriptAsync($"try {{ if ({ (shouldMute ? "true" : "false") }) player.mute(); else player.unMute(); }} catch(e) {{}}");
                _isMuted = shouldMute;
                UpdateMuteIcon();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExpandedCard] Mute Error: {ex.Message}");
            }
        }
        
        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            await SetMutedAsync(!_isMuted);
        }
        
        private void UpdateMuteIcon()
        {
            // E74F = Volume, E74E = Mute
            MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        }

        // Color Adaptation Public Method
        public void SetAmbienceColor(Color color)
        {
            if (AmbienceGrid.Background is RadialGradientBrush brush)
            {
                if (brush.GradientStops.Count > 0)
                {
                    brush.GradientStops[0].Color = color;
                }
            }
        }
        private void UpdateUiFromUnified(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified, string? overrideTitle = null, string? overrideSubtitle = null, string? overrideOverview = null, string? overrideBackdrop = null)
        {
            if (unified == null) 
            {
                System.Diagnostics.Debug.WriteLine("[EXP-UI] Update Aborted: unified is null");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[EXP-UI] Updating UI for: {unified.Title} | ID: {unified.MetadataId} | Source: {unified.DataSource}");
            
            bool isDedupe = _lastMetadata?.MetadataId == unified.MetadataId;
            _lastMetadata = unified;
            System.Diagnostics.Debug.WriteLine($"[EXP-DATA-FLOW] Processing data: Genres='{unified.Genres}', OverviewLength={unified.Overview?.Length ?? 0}, Year='{unified.Year}'");

            // 1. Identity
            string logoUrl = unified.LogoUrl ?? "";
            bool logoUrlChanged = _currentLogoUrl != logoUrl;
            SafeSetImage(LogoImage, logoUrl, ref _currentLogoUrl);

            bool hasLogo = !string.IsNullOrEmpty(logoUrl);
            LogoContainer.Visibility = hasLogo ? Visibility.Visible : Visibility.Collapsed;

            // Always set title text
            SafeSetText(TitleText, overrideTitle ?? unified.Title);

            // [FIX] VISIBILITY SEEDING:
            // Only force Title visible if we don't have a logo, or if this is a fresh item, or the logo changed.
            // This prevents 'Authoritative' updates from overriding the 'WIDE' logo decision made by ImageOpened.
            if (!hasLogo || !isDedupe || logoUrlChanged)
            {
                TitleText.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine("[EXP-LAYOUT] 0ms Seed - Title Visible forced");
            }


            // 2. Metadata & Description
            SafeSetText(GenresText, overrideSubtitle ?? unified.Genres);
            SafeSetText(DescText, overrideOverview ?? unified.Overview);
            WritersText.Visibility = Visibility.Collapsed; 

            SafeSetText(RatingText, unified.Rating > 0 ? $"\u2605 {unified.Rating:F1}" : "");
            SafeSetText(YearText, unified.Year);
            
            RatingText.Visibility = unified.Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
            YearText.Visibility = string.IsNullOrEmpty(unified.Year) ? Visibility.Collapsed : Visibility.Visible;

            // 3. Badges, Secondary Data & Progress
            UpdateTechnicalBadges(_stream);
            UpdatePlayButton(_stream);
            UpdateProgressState(_stream);

            // Age & Country (Static Metadata)
            string cert = !string.IsNullOrEmpty(unified.Certification) ? unified.Certification : (unified.AgeRating ?? "");
            bool hasAge = !string.IsNullOrEmpty(cert);
            bool hasCountry = !string.IsNullOrEmpty(unified.Country);

            if (BadgeAge != null)
            {
                BadgeAge.Visibility = hasAge ? Visibility.Visible : Visibility.Collapsed;
                SafeSetText(BadgeAgeText, cert);
            }
            if (BadgeCountry != null)
            {
                BadgeCountry.Visibility = hasCountry ? Visibility.Visible : Visibility.Collapsed;
                SafeSetText(BadgeCountryText, unified.Country);
            }
            if (StaticMetadataPanel != null)
                StaticMetadataPanel.Visibility = (hasAge || hasCountry) ? Visibility.Visible : Visibility.Collapsed;
            
            _ = UpdateCreditsAsync(unified);

            // 4. State & Background
            MainSkeleton.Visibility = Visibility.Collapsed;
            BadgeSkeleton.Visibility = Visibility.Collapsed;
            RealContentPanel.Opacity = 1; 
            SafeSetImage(BackdropImage, overrideBackdrop ?? unified.BackdropUrl, ref _currentBackdropUrl);

            if (unified.Rating > 8.0)
            {
                MoodTag.Visibility = Visibility.Visible;
                SafeSetText(MoodText, "Top Rated");
            }

            // 5. ANIMATION: One-shot reveal per item to prevent double-fades
            if (!_isRevealed)
            {
                _isRevealed = true;
                DispatcherQueue.TryEnqueue(() => StaggeredRevealContent());
            }

            ToolTipService.SetToolTip(PlayButton, _stream?.Title);
        }


        private void LogoImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img && img.Source is BitmapImage bmp)
            {
                var width = bmp.PixelWidth;
                var height = bmp.PixelHeight;
                if (height > 0)
                {
                    double aspect = (double)width / height;
                    System.Diagnostics.Debug.WriteLine("[EXP-LAYOUT] Logo Opened - Pixels: {width}x{height} | Aspect: {aspect:F2}");
                    if (aspect < 2.0) // Boxy/Square/Portrait
                    {
                        System.Diagnostics.Debug.WriteLine("[EXP-LAYOUT] Final Decision: BOXY - Keeping Title Visible");
                        TitleText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        
                        // [Compact] Reduce icon height for square/portrait to save vertical space
                        if (aspect < 1.4)
                        {
                            img.MaxHeight = 38;
                        }
                        else
                        {
                            img.MaxHeight = 55;
                        }
                    }
                    else // Ultra-Landscape (Title Logo)
                    {
                        System.Diagnostics.Debug.WriteLine("[EXP-LAYOUT] Final Decision: WIDE - Hiding Title");
                        TitleText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        img.MaxHeight = 55;
                    }

                    // Always center branding elements if a logo exists
                    LogoContainer.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                    TitleText.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center;
                    TitleText.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
                }
            }
            ApplyLogoShadow();
        }

        private void ApplyLogoShadow()
        {
            try
            {
                if (_compositor == null) return;
                if (LogoImage.Source == null) return;

                var visual = ElementCompositionPreview.GetElementVisual(LogoContainer);
                if (visual == null) return;
                
                var shadow = _compositor.CreateDropShadow();
                
                shadow.Color = Color.FromArgb(160, 0, 0, 0);
                shadow.BlurRadius = 15f;
                shadow.Offset = new System.Numerics.Vector3(0, 5, 0);
                
                // Important: Mask the shadow with the actual image content
                shadow.Mask = LogoImage.GetAlphaMask();
                
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Shadow = shadow;
                
                // Sync shadow container size with image container size
                var bindSize = _compositor.CreateExpressionAnimation("visual.Size");
                bindSize.SetReferenceParameter("visual", visual);
                sprite.StartAnimation("Size", bindSize);
                
                // Attach shadow to the dedicated host (background layer)
                ElementCompositionPreview.SetElementChildVisual(LogoShadowHost, sprite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExpandedCard] Failed to apply logo shadow: {ex.Message}");
            }
        }

        private async Task UpdateCreditsAsync(ModernIPTVPlayer.Models.Metadata.UnifiedMetadata unified)
        {
            if (unified == null) return;
            
            try
            {
                var dispatcher = this.DispatcherQueue;
                if (dispatcher == null) return;

                List<CastItem> castItems = new();
                List<CastItem> directorItems = new();

                // 1. Gather Cast from Seed Metadata
                if (unified.Cast != null && unified.Cast.Count > 0)
                {
                    foreach (var c in unified.Cast.Take(12))
                        castItems.Add(CreateCastItem(c.Name, c.ProfileUrl));
                }

                // 2. Gather Directors from Seed Metadata
                if (unified.Directors != null && unified.Directors.Count > 0)
                {
                    foreach (var d in unified.Directors.Take(5))
                        directorItems.Add(CreateCastItem(d.Name, d.ProfileUrl));
                }

                // 3. TMDB Fallback for Missing Information
                if ((castItems.Count == 0 || directorItems.Count == 0) && unified.TmdbInfo != null && ModernIPTVPlayer.AppSettings.IsTmdbEnabled)
                {
                    var credits = await TmdbHelper.GetCreditsAsync(unified.TmdbInfo.Id, unified.IsSeries);
                    if (credits != null)
                    {
                        if (castItems.Count == 0 && credits.Cast != null)
                        {
                            foreach (var c in credits.Cast.Take(12))
                                castItems.Add(CreateCastItem(c.Name, c.FullProfileUrl));
                        }
                        if (directorItems.Count == 0 && credits.Crew != null)
                        {
                            var directors = credits.Crew.Where(crew => crew.Job == "Director").Take(5);
                            foreach (var d in directors)
                                directorItems.Add(CreateCastItem(d.Name, d.FullProfileUrl));
                        }
                    }
                }

                dispatcher.TryEnqueue(() => {
                    // Safety check: is the card still showing the same content?
                    if (_lastMetadata != unified) return;

                    // Update Cast Section
                    CastList.Clear();
                    foreach (var item in castItems) CastList.Add(item);
                    
                    bool hasCast = CastList.Count > 0;
                    CastHeaderText.Visibility = hasCast ? Visibility.Visible : Visibility.Collapsed;
                    CastListView.Visibility = hasCast ? Visibility.Visible : Visibility.Collapsed;
                    if (hasCast) CastListView.ItemsSource = CastList;

                    // Update Directors Section
                    DirectorList.Clear();
                    foreach (var item in directorItems) DirectorList.Add(item);

                    bool hasDirectors = DirectorList.Count > 0;
                    DirectorHeaderText.Visibility = hasDirectors ? Visibility.Visible : Visibility.Collapsed;
                    DirectorListView.Visibility = hasDirectors ? Visibility.Visible : Visibility.Collapsed;
                    if (hasDirectors) DirectorListView.ItemsSource = DirectorList;
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Credits Update Error: {ex.Message}");
            }
        }

        private CastItem CreateCastItem(string name, string? profileUrl)
        {
            string initials = "";
            if (!string.IsNullOrWhiteSpace(name))
            {
                var parts = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    initials = parts[0][0].ToString();
                    if (parts.Length > 1) initials += parts[parts.Length - 1][0];
                }
            }

            // Neutral Dark Gray for a professional, unified look across all platform items.
            var neutralColor = AppColorHelper.FromHex("#333333");

            // Only set URL if it's a valid absolute URI to avoid conversion crashes in XAML
            string? safeUrl = null;
            if (!string.IsNullOrWhiteSpace(profileUrl) && Uri.TryCreate(profileUrl, UriKind.Absolute, out _))
            {
                safeUrl = profileUrl;
            }

            return new CastItem 
            { 
                Name = name, 
                FullProfileUrl = safeUrl,
                Initials = initials.ToUpperInvariant(),
                ProfileBackground = new SolidColorBrush(neutralColor)
            };
        }

        private void StaggeredRevealContent()
        {
            if (_compositor == null || RealContentPanel == null) return;

            double delay = 0;
            const double staggerIncrement = 0.08; 

            foreach (var child in RealContentPanel.Children)
            {
                if (child is UIElement element)
                {
                    var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
                    
                    // CRITICAL: Set Visual Opacity to 0 initially
                    visual.Opacity = 0f;

                    try 
                    {
                        var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                        fadeIn.InsertKeyFrame(1f, 1f);
                        fadeIn.Duration = TimeSpan.FromMilliseconds(400);
                        fadeIn.DelayTime = TimeSpan.FromSeconds(delay);

                        // Define MoveUp Animation
                        var moveUp = _compositor.CreateVector3KeyFrameAnimation();
                        moveUp.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 8, 0));
                        moveUp.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                        moveUp.Duration = TimeSpan.FromMilliseconds(500);
                        moveUp.DelayTime = TimeSpan.FromSeconds(delay);

                        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                        
                        try { visual.StartAnimation("Opacity", fadeIn); } catch { }
                        try { visual.StartAnimation("Translation", moveUp); } catch { }
                    }
                    catch (Exception ex)
                    {
                         System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Staggered Animation Error: {ex.Message}");
                         visual.Opacity = 1f; // Ensure visible if animation fails
                    }

                    delay += staggerIncrement;
                }
            }
        }

        private HistoryItem? GetHistoryForStream(IMediaStream stream)
        {
            if (stream == null) return null;
            
            string id = "";
            bool isSeries = false;

            if (stream is LiveStream live) id = live.StreamId.ToString();
            else if (stream is SeriesStream series) { id = series.SeriesId.ToString(); isSeries = true; }
            else if (stream is StremioMediaStream s) { id = s.Meta.Id; isSeries = s.Meta.Type == "series" || s.Meta.Type == "tv"; }
            else if (stream is WatchlistItem w) { id = w.Id; isSeries = w.Type == "series"; }

            if (string.IsNullOrEmpty(id)) return null;
            return isSeries ? HistoryManager.Instance.GetLastWatchedEpisode(id) : HistoryManager.Instance.GetProgress(id);
        }

        private void UpdatePlayButton(IMediaStream stream)
        {
            if (stream == null) return;
            
            bool isResume = false;
            string subtext = null;

            var history = GetHistoryForStream(stream);
            if (history != null && !history.IsFinished)
            {
                // For movies/lives, check threshold. For episodes, always resume.
                if (history.Duration > 0)
                {
                    if ((history.Position / (double)history.Duration) > 0.005) isResume = true;
                }
                else 
                {
                    isResume = true;
                }

                if (isResume && history.EpisodeNumber > 0)
                {
                    int displayEp = history.EpisodeNumber == 0 ? 1 : history.EpisodeNumber;
                    subtext = $"S{history.SeasonNumber:D2}E{displayEp:D2}";
                }
            }

            if (isResume)
            {
                PlayButtonText.Text = "Devam Et";
                if (!string.IsNullOrEmpty(subtext))
                {
                    PlayButtonSubtext.Text = subtext;
                    PlayButtonSubtext.Visibility = Visibility.Visible;
                }
                else
                {
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
            }
            else if (_isSeriesFinished)
            {
                PlayButtonText.Text = "Tekrar İzle";
                PlayButtonSubtext.Text = "Tüm bölümler izlendi";
                PlayButtonSubtext.Visibility = Visibility.Visible;
            }
            else
            {
                PlayButtonText.Text = "Oynat";
                
                // Show Runtime as subtext if available and not in resume/finished state
                if (_lastMetadata != null && !string.IsNullOrEmpty(_lastMetadata.Runtime))
                {
                    PlayButtonSubtext.Text = _lastMetadata.Runtime;
                    PlayButtonSubtext.Visibility = Visibility.Visible;
                }
                else
                {
                    PlayButtonSubtext.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayClicked?.Invoke(this, EventArgs.Empty);
        
        private void DetailsButton_Click(object sender, RoutedEventArgs e) 
        {
            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        private void DetailsArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) 
        {
            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        private void TrailerArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Ignore if clicked on MuteButton or its children
            if (e.OriginalSource is DependencyObject obj)
            {
                var parent = obj;
                while (parent != null && parent != TrailerArea)
                {
                    if (parent == MuteButton || parent == ExpandButton) return;
                    parent = VisualTreeHelper.GetParent(parent);
                }
            }

            PrepareConnectedAnimation();
            DetailsClicked?.Invoke(this, _tmdbInfo);
        }

        public void PrepareConnectedAnimation()
        {
            Microsoft.UI.Xaml.Media.Animation.ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate("ForwardConnectedAnimation", BackdropImage);
        }



        private void UpdateProgressState(IMediaStream stream)
        {
            if (stream == null) return;

            var history = GetHistoryForStream(stream);

            if (history != null && !history.IsFinished && history.Duration > 0)
            {
                double pct = (history.Position / history.Duration) * 100;
                if (pct > 0.5) // Show if more than 0.5% watched
                {
                    PlaybackProgressBar.Value = pct;
                    PlaybackProgressBar.Visibility = Visibility.Visible;

                    var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                    string timeLeft = remaining.TotalHours >= 1
                        ? $"{(int)remaining.TotalHours}sa {remaining.Minutes}dk kaldı"
                        : $"{remaining.Minutes}dk kaldı";

                    // Premium Placement: Integrate into Play Button subtext
                    if (PlayButtonText.Text == "Devam Et")
                    {
                        string baseText = PlayButtonSubtext.Text;
                        if (!string.IsNullOrEmpty(baseText) && !baseText.Contains(timeLeft))
                        {
                             // If it already has S01E05, append.
                             PlayButtonSubtext.Text = $"{baseText} • {timeLeft}";
                        }
                        else if (string.IsNullOrEmpty(baseText))
                        {
                            PlayButtonSubtext.Text = timeLeft;
                        }
                        PlayButtonSubtext.Visibility = Visibility.Visible;
                    }

                    // TimeLeftText.Text = timeLeft;
                    // ProgressPanel.Visibility = Visibility.Visible;
                    return;
                }
            }

            PlaybackProgressBar.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        private void SafeSetText(TextBlock? target, string? newValue)
        {
            if (target == null) return;
            string val = newValue ?? "";
            if (target.Text != val) target.Text = val;
        }

        private void SafeSetImage(Image? target, string? newUrl, ref string? currentUrl)
        {
            if (target == null) return;
            if (string.IsNullOrEmpty(newUrl))
            {
                if (target.Source != null) target.Source = null;
                currentUrl = null;
                return;
            }

            if (currentUrl != newUrl)
            {
                currentUrl = newUrl;
                try
                {
                    if (Uri.TryCreate(newUrl, UriKind.Absolute, out var uri))
                    {
                        target.Source = new BitmapImage(uri);
                    }
                    else
                    {
                        target.Source = null;
                        currentUrl = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Image Error ({newUrl}): {ex.Message}");
                    target.Source = null;
                    currentUrl = null;
                }
            }
        }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class CastItem
    {
        public string Name { get; set; }
        private string? _fullProfileUrl;
        public string? FullProfileUrl 
        { 
            get => _fullProfileUrl; 
            set => _fullProfileUrl = string.IsNullOrWhiteSpace(value) ? null : value; 
        }
        public string Initials { get; set; }
        public SolidColorBrush ProfileBackground { get; set; }
        public Visibility ImageVisibility => string.IsNullOrEmpty(FullProfileUrl) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility InitialsVisibility => string.IsNullOrEmpty(FullProfileUrl) ? Visibility.Visible : Visibility.Collapsed;
    }
}
