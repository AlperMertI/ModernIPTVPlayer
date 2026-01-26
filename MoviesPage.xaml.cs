using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Windows.UI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using ModernIPTVPlayer.Controls;
using System.Numerics;

namespace ModernIPTVPlayer
{
    public sealed partial class MoviesPage : Page
    {
        private LoginParams? _loginInfo;
        private HttpClient _httpClient;
        // _currentBackdropColor removed
        // _colorCache removed
        // _lastProcessedImageUrl removed
        public MoviesPage()
        {
            this.InitializeComponent();
            _httpClient = HttpHelper.Client;
        }
        


        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is LoginParams p)
            {
                if (_loginInfo != null && _loginInfo.PlaylistUrl != p.PlaylistUrl)
                {
                    CategoryListView.ItemsSource = null;
                    MovieGridView.ItemsSource = null;
                }
                _loginInfo = p;
            }

            if (_loginInfo != null && !string.IsNullOrEmpty(_loginInfo.Host))
            {
                if (CategoryListView.ItemsSource == null)
                {
                    await LoadVodCategoriesAsync();
                }
            }
            
            // OPTIMIZATION: Warm up the trailer player after 3 seconds of idle time.
            // This ensures the WebView is initialized by the time the user hovers a movie.
            var warmupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            warmupTimer.Tick += (s, args) =>
            {
                warmupTimer.Stop();
                ActiveExpandedCard?.PrepareForTrailer();
            };
            warmupTimer.Start();
        }

        private List<LiveCategory> _allCategories = new();

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
        }

        private void CategorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allCategories == null) return;
            
            var query = CategorySearchBox.Text.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(query))
            {
                CategoryListView.ItemsSource = _allCategories;
            }
            else
            {
                var filtered = new List<LiveCategory>();
                foreach (var cat in _allCategories)
                {
                    if (cat.CategoryName != null && cat.CategoryName.ToLowerInvariant().Contains(query))
                    {
                        filtered.Add(cat);
                    }
                }
                CategoryListView.ItemsSource = filtered;
            }
        }

        // ==========================================
        // Premium Search Box Interactions
        // ==========================================
        private void SearchPill_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (CategorySearchBox.FocusState == FocusState.Unfocused)
            {
                SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
            }
        }

        private void SearchPill_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (CategorySearchBox.FocusState == FocusState.Unfocused)
            {
                SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
            }
        }

        private void CategorySearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            SearchPillBorder.BorderBrush = (Brush)Application.Current.Resources["SystemControlHighlightAccentBrush"];
        }

        private void CategorySearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            SearchPillBorder.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
            SearchPillBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        }

        private async Task LoadVodCategoriesAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                CategoryListView.ItemsSource = null;
                MovieGridView.ItemsSource = null;
                _allCategories.Clear();

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_categories";

                // DEBUG: Show URL
                System.Diagnostics.Debug.WriteLine($"DEBUG LOAD: {api}");

                string json = await _httpClient.GetStringAsync(api);
                
                // DEBUG: Show JSON length
                System.Diagnostics.Debug.WriteLine($"DEBUG JSON LEN: {json?.Length ?? 0}");

                if (string.IsNullOrEmpty(json))
                {
                    ShowMessageDialog("Hata", "API'den boş cevap döndü.");
                    return;
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var categories = JsonSerializer.Deserialize<List<LiveCategory>>(json, options);

                if (categories != null)
                {
                    System.Diagnostics.Debug.WriteLine($"DEBUG CAT COUNT: {categories.Count}");
                    _allCategories = categories;
                    CategoryListView.ItemsSource = _allCategories;
                    
                    // Auto-select first category
                    if (_allCategories.Count > 0)
                    {
                        CategoryListView.SelectedIndex = 0;
                        await LoadVodStreamsAsync(_allCategories[0]);
                    }
                }
                else
                {
                    ShowMessageDialog("Hata", "JSON parse edildi ama kategori listesi boş (null).");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movie categories: {ex.Message}");
                ShowMessageDialog("Kritik Hata", $"Veri çekilirken hata oluştu:\n{ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
        }

        private List<int> _skeletonList = new List<int>(new int[20]); // 20 placeholders

        private async Task LoadVodStreamsAsync(LiveCategory category)
        {
            // Update Title when category selected
            SelectedCategoryTitle.Text = category.CategoryName;

            if (category.Channels != null && category.Channels.Count > 0)
            {
                MovieGridView.ItemsSource = category.Channels;
                 // Ensure Grid is visible and Skeleton hidden
                MovieGridView.Visibility = Visibility.Visible;
                SkeletonGrid.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                // SHOW SKELETON
                MovieGridView.Visibility = Visibility.Collapsed;
                SkeletonGrid.Visibility = Visibility.Visible;
                SkeletonGrid.ItemsSource = _skeletonList;
                
                MovieGridView.ItemsSource = null;

                string baseUrl = _loginInfo.Host.TrimEnd('/');
                string api = $"{baseUrl}/player_api.php?username={_loginInfo.Username}&password={_loginInfo.Password}&action=get_vod_streams&category_id={category.CategoryId}";

                string json = await _httpClient.GetStringAsync(api);
                
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var streams = JsonSerializer.Deserialize<List<LiveStream>>(json, options);

                if (streams != null)
                {
                    foreach (var s in streams)
                    {
                        string extension = !string.IsNullOrEmpty(s.ContainerExtension) ? s.ContainerExtension : "mp4";
                        s.StreamUrl = $"{baseUrl}/movie/{_loginInfo.Username}/{_loginInfo.Password}/{s.StreamId}.{extension}"; 
                    }

                    category.Channels = streams;
                    MovieGridView.ItemsSource = category.Channels;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading movies: {ex.Message}");
            }
            finally
            {
                // HIDE SKELETON
                SkeletonGrid.Visibility = Visibility.Collapsed;
                MovieGridView.Visibility = Visibility.Visible;
            }
        }
        
        private void Image_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                img.Opacity = 0;
                var anim = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromSeconds(0.6),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                
                var sb = new Storyboard();
                sb.Children.Add(anim);
                Storyboard.SetTarget(anim, img);
                Storyboard.SetTargetProperty(anim, "Opacity");
                sb.Begin();
            }
        }

        private async void CategoryListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            // Reset Scroll to Top
            // Note: GridView doesn't have a direct ScrollToTop, but setting ItemsSource usually resets it.
            // If needed we can find the ScrollViewer.
            
            if (e.ClickedItem is LiveCategory category)
            {
                await LoadVodStreamsAsync(category);
            }
        }

        private void MovieGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is LiveStream stream)
            {
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(stream.StreamUrl, stream.Name));
            }
        }

        private void PosterCard_ColorsExtracted(object sender, (Windows.UI.Color Primary, Windows.UI.Color Secondary) colors)
        {
             // Update the global backdrop when a poster decides its colors are ready/hovered
             BackdropControl.TransitionTo(colors.Primary, colors.Secondary);
        }

        // ==========================================
        // EXPANDED CARD LOGIC
        // ==========================================
        
        private DispatcherTimer _hoverTimer;
        private PosterCard _pendingHoverCard;
        private System.Threading.CancellationTokenSource _closeCts;
        
        private DispatcherTimer _flightTimer;
        
        private void ExpandedCard_HoverStarted(object sender, EventArgs e)
        {
            if (sender is PosterCard card)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] HoverStarted on: {((LiveStream)card.DataContext).Name}");
                
                // CRITICAL: If we moved to a new card, CANCEL any pending close logic (C# Task)
                if (_closeCts != null)
                {
                    System.Diagnostics.Debug.WriteLine("[ExpandedCard] Cancelling pending close task.");
                    _closeCts.Cancel();
                }

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                
                // ALSO CRITICAL: Stop the composition animation running on the visual (GPU)
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Scale");
                
                // If it was already visible, keep it that way for morphing
                bool isAlreadyOpen = ActiveExpandedCard.Visibility == Visibility.Visible;
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] State check - Visibility: {ActiveExpandedCard.Visibility}, Opacity: {visual.Opacity}, isAlreadyOpen: {isAlreadyOpen}");

                if (isAlreadyOpen)
                {
                    // Ensure it stays visible and opaque during the flight timer
                    visual.Opacity = 1f;
                    
                    // Throttle the fast path slightly (350ms) to avoid triggering on every single card crossed during fast movement
                    if (_flightTimer == null) 
                    {
                        _flightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                        _flightTimer.Tick += FlightTimer_Tick;
                    }
                    else
                    {
                        _flightTimer.Stop(); // Reset logic
                    }
                    
                    _pendingHoverCard = card;
                    _flightTimer.Start();
                }
                else
                {
                    // SLOW PATH: Opening from scratch. Use debounce.
                    _pendingHoverCard = card;
                    if (_hoverTimer == null)
                    {
                        _hoverTimer = new DispatcherTimer();
                        _hoverTimer.Interval = TimeSpan.FromMilliseconds(600); // 600ms debounce
                        _hoverTimer.Tick += HoverTimer_Tick;
                    }
                    else
                    {
                        _hoverTimer.Stop();
                    }
                    _hoverTimer.Start();
                    
                    // Predictive prefetch
                    ActiveExpandedCard.PrepareForTrailer();
                }
            }
        }

        private void FlightTimer_Tick(object sender, object e)
        {
            _flightTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered)
            {
                 System.Diagnostics.Debug.WriteLine("[ExpandedCard] Flight Timer Triggered - Morphing...");
                 ShowExpandedCard(_pendingHoverCard);
            }
        }

        private void HoverTimer_Tick(object sender, object e)
        {
            _hoverTimer.Stop();
            if (_pendingHoverCard != null && _pendingHoverCard.IsHovered) // Only show if still hovering
            {
                System.Diagnostics.Debug.WriteLine("[ExpandedCard] Hover Timer Triggered - Popping...");
                ShowExpandedCard(_pendingHoverCard);
            }
        }

        private async void ShowExpandedCard(PosterCard sourceCard)
        {
            try
            {
                _closeCts?.Cancel();
                _closeCts = new System.Threading.CancellationTokenSource();

                // 1. Get Coordinates
                var transform = sourceCard.TransformToVisual(OverlayCanvas);
                var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                
                double widthDiff = 320 - sourceCard.ActualWidth;
                double heightDiff = 420 - sourceCard.ActualHeight;
                
                double targetX = position.X - (widthDiff / 2);
                double targetY = position.Y - (heightDiff / 2);

                // Boundary Checks
                if (targetX < 10) targetX = 10;
                if (targetX + 320 > OverlayCanvas.ActualWidth) targetX = OverlayCanvas.ActualWidth - 330;
                if (targetY < 10) targetY = 10;
                if (targetY + 420 > OverlayCanvas.ActualHeight) targetY = OverlayCanvas.ActualHeight - 430;

                var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                var compositor = visual.Compositor;

                // 0. Enable Translation
                ElementCompositionPreview.SetIsTranslationEnabled(ActiveExpandedCard, true);

                // 2. Handle State (Pop vs Morph)
                bool isMorph = ActiveExpandedCard.Visibility == Visibility.Visible && visual.Opacity > 0.1f;
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Show Card - TargetLayout: ({targetX:F2}, {targetY:F2}), CurrentLayout: ({Canvas.GetLeft(ActiveExpandedCard):F2}, {Canvas.GetTop(ActiveExpandedCard):F2}), VisualOpacity: {visual.Opacity:F2}, isMorph: {isMorph}");

                if (isMorph)
                {
                    // --- MORPH MODE ---
                    ActiveExpandedCard.StopTrailer();

                    double oldLeft = Canvas.GetLeft(ActiveExpandedCard);
                    double oldTop = Canvas.GetTop(ActiveExpandedCard);
                    
                    // Update Layout Position
                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    // Force update layout? XAML usually handles this.
                    ActiveExpandedCard.UpdateLayout(); 

                    float deltaX = (float)(oldLeft - targetX);
                    float deltaY = (float)(oldTop - targetY);
                    
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Morph Calculation: Old({oldLeft:F2}) - New({targetX:F2}) = Delta({deltaX:F2})");
                    
                    // Set TRANSLATION to compensate (Layout has moved to target, so we translate back to old position)
                    visual.Properties.InsertVector3("Translation", new Vector3(deltaX, deltaY, 0));
                    System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Set Visual.Translation = {deltaX}, {deltaY}");
                    
                    var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
                    offsetAnim.Target = "Translation";
                    var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1.0f));
                    offsetAnim.InsertKeyFrame(1.0f, Vector3.Zero, easing);
                    offsetAnim.Duration = TimeSpan.FromMilliseconds(400);
                    
                    visual.StartAnimation("Translation", offsetAnim);
                    
                    // Ensure full visibility
                    visual.Opacity = 1f;
                    visual.Scale = new Vector3(1f, 1f, 1f);
                }
                else
                {
                    // --- POP MODE ---
                    System.Diagnostics.Debug.WriteLine("[ExpandedCard] Pop Mode - Reseting Translation to Zero.");
                    visual.StopAnimation("Translation");
                    visual.StopAnimation("Offset");
                    visual.Properties.InsertVector3("Translation", Vector3.Zero);
                    visual.Scale = new Vector3(0.8f, 0.8f, 1f);
                    visual.Opacity = 0;

                    Canvas.SetLeft(ActiveExpandedCard, targetX);
                    Canvas.SetTop(ActiveExpandedCard, targetY);
                    ActiveExpandedCard.Visibility = Visibility.Visible;

                    var springAnim = compositor.CreateSpringVector3Animation();
                    springAnim.Target = "Scale";
                    springAnim.FinalValue = new Vector3(1f, 1f, 1f);
                    springAnim.DampingRatio = 0.7f;
                    springAnim.Period = TimeSpan.FromMilliseconds(50);
                    
                    var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
                    fadeAnim.Target = "Opacity";
                    fadeAnim.InsertKeyFrame(1f, 1f);
                    fadeAnim.Duration = TimeSpan.FromMilliseconds(200);

                    visual.StartAnimation("Scale", springAnim);
                    visual.StartAnimation("Opacity", fadeAnim);
                }

                // 3. Load Data
                if (sourceCard.DataContext is LiveStream stream)
                {
                    await ActiveExpandedCard.LoadDataAsync(stream);
                }

                ActiveExpandedCard.PointerExited -= ActiveExpandedCard_PointerExited;
                ActiveExpandedCard.PointerExited += ActiveExpandedCard_PointerExited;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Error Showing: {ex.Message}");
            }
        }

        private async void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // If the card is visible and mouse leaves the window/root grid, close it.
            if (ActiveExpandedCard.Visibility == Visibility.Visible)
            {
                await CloseExpandedCardAsync();
            }
        }

        private async void ActiveExpandedCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
             await CloseExpandedCardAsync();
        }

        private async Task CloseExpandedCardAsync()
        {
             try 
             {
                 System.Diagnostics.Debug.WriteLine("[ExpandedCard] Close Request - Sequence Start.");
                 _closeCts?.Cancel();
                 _closeCts = new System.Threading.CancellationTokenSource();
                 var token = _closeCts.Token;

                 ActiveExpandedCard.StopTrailer();

                 await Task.Delay(50, token);
                 
                 if (token.IsCancellationRequested) 
                 {
                     System.Diagnostics.Debug.WriteLine("[ExpandedCard] Exit task cancelled (Switch detected).");
                     return;
                 }

                 System.Diagnostics.Debug.WriteLine("[ExpandedCard] Exit animation starting...");
                 var visual = ElementCompositionPreview.GetElementVisual(ActiveExpandedCard);
                 var compositor = visual.Compositor;
                 
                 var fadeOut = compositor.CreateScalarKeyFrameAnimation();
                 fadeOut.Target = "Opacity";
                 fadeOut.InsertKeyFrame(1f, 0f);
                 fadeOut.Duration = TimeSpan.FromMilliseconds(200);
                 
                 var scaleDown = compositor.CreateVector3KeyFrameAnimation();
                 scaleDown.Target = "Scale";
                 scaleDown.InsertKeyFrame(1f, new Vector3(0.95f, 0.95f, 1f));
                 scaleDown.Duration = TimeSpan.FromMilliseconds(200);
                 
                 visual.StartAnimation("Opacity", fadeOut);
                 visual.StartAnimation("Scale", scaleDown);
                 
                 await Task.Delay(200, token);
                 if (token.IsCancellationRequested) return;

                 System.Diagnostics.Debug.WriteLine("[ExpandedCard] Setting Visibility = Collapsed.");
                 ActiveExpandedCard.Visibility = Visibility.Collapsed;
             }
             catch (TaskCanceledException)
             {
                 System.Diagnostics.Debug.WriteLine("[ExpandedCard] Close cancelled - Switched!");
             }
             catch (Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"[ExpandedCard] Close Error: {ex.Message}");
             }
        }

        // Expanded Card Events
        private void ExpandedCard_PlayClicked(object sender, EventArgs e)
        {
            ShowMessageDialog("Play", "Video Player Starting...");
        }

        private void ExpandedCard_DetailsClicked(object sender, EventArgs e)
        {
            ShowMessageDialog("Details", "Navigating to Details Page...");
        }

        private void ExpandedCard_AddListClicked(object sender, EventArgs e)
        {
             ShowMessageDialog("Favorites", "Added to your list.");
        }



        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder for Global Spotlight Search
            ContentDialog searchDialog = new ContentDialog
            {
                Title = "Global Arama",
                Content = "Bu özellik (Spotlight Search) yakında eklenecek.",
                CloseButtonText = "Kapat",
                XamlRoot = this.XamlRoot
            };
            await searchDialog.ShowAsync();
        }

        // TODO: Implement UpdateDynamicBackdrop(string imageUrl) when ready
        private async void ShowMessageDialog(string title, string content)
        {
            if (this.XamlRoot == null) return;
            ContentDialog dialog = new ContentDialog 
            { 
                Title = title, 
                Content = content, 
                CloseButtonText = "Tamam", 
                XamlRoot = this.XamlRoot 
            };
            await dialog.ShowAsync();
        }
        

    }
}
