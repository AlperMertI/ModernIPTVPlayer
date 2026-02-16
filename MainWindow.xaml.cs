using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace ModernIPTVPlayer
{
    public sealed partial class MainWindow : Window
    {
        public new static MainWindow Current { get; private set; }
        
        public UIElement TitleBarElement => AppTitleBar;
        public MainWindow()
        {
            Current = this;
            this.InitializeComponent();

            _compositor = ElementCompositionPreview.GetElementVisual(this.Content).Compositor;

            InitializeDownloadManager();
            
            // Set title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Default navigation based on settings
            string startupPageTag = AppSettings.DefaultStartupPage;
            Type startupPageType = GetPageTypeFromTag(startupPageTag);

            // Navigate initial
            ContentFrame.Navigate(startupPageType, App.CurrentLogin);

            // Sync initial button and pill
            this.SizeChanged += (s, e) => UpdatePillPosition(GetActiveButton());
            
            // Initialize sidebar state
            RootGrid.Loaded += (s, e) => AnimateSidebar(60);

            _compositor = ElementCompositionPreview.GetElementVisual(this.Content).Compositor;

            // Auto-Restore Opacity when Window is Activated
            this.Activated += MainWindow_Activated;

           InitializeSidebarBehavior();
        }

        private DispatcherTimer _sidebarHideTimer;
        private bool _isSidebarVisible = true;

        private void InitializeSidebarBehavior()
        {
            _sidebarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _sidebarHideTimer.Tick += (s, e) => HideSidebar();
            _sidebarHideTimer.Start();
        }

        private Compositor _compositor;

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(RootGrid).Position;
            // Show if mouse is near left edge ( < 10px ) and sidebar is hidden
            if (!_isSidebarVisible && point.X < 10)
            {
                ShowSidebar();
            }
        }

        private void HideSidebar()
        {
            if (!_isSidebarVisible) return;
            _isSidebarVisible = false;
            
            _sidebarHideTimer.Stop();

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = -80, // Hide completely (Width 60 + Margin)
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuinticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SidebarTranslate);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
            sb.Children.Add(anim);
            sb.Begin();
        }

        private void ShowSidebar()
        {
            if (_isSidebarVisible) return;
            _isSidebarVisible = true;
            
            _sidebarHideTimer.Stop(); // Wait for interaction before restarting text
            
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuinticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SidebarTranslate);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
            sb.Children.Add(anim);
            sb.Begin();
            
            // Restart timer only when mouse leaves
        }

        private RadioButton GetActiveButton()
        {
            if (ContentFrame.Content == null) return null;
            string tag = ContentFrame.Content.GetType().Name;
            // Handle some tag/name mismatches if any
            if (tag == "AddonsPage") tag = "AddonsPage"; // Namespace check?

            return NavButtonsStack.Children.OfType<RadioButton>()
                .FirstOrDefault(b => b.Tag?.ToString() == tag);
        }

        private void NavButton_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton btn && btn.Tag is string tag)
            {
                Type? pageType = GetPageTypeFromTag(tag);
                if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                {
                    ContentFrame.Navigate(pageType, App.CurrentLogin);
                }
                UpdatePillPosition(btn);
            }
        }

        private void UpdatePillPosition(RadioButton target)
        {
            if (target == null || _compositor == null) return;

            // Ensure layout is updated to get positions
            target.UpdateLayout();

            // Transform relative to NavIndicator's parent
            var transform = target.TransformToVisual((UIElement)NavIndicator.Parent);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            double targetY = point.Y;

            // Use Composition animation for perfect smoothness
            var indicatorVisual = ElementCompositionPreview.GetElementVisual(NavIndicator);
            
            if (NavIndicator.Opacity == 0)
            {
                NavIndicator.Opacity = 1;
                // Note: Offset is a Vector3
                indicatorVisual.Offset = new Vector3(indicatorVisual.Offset.X, (float)targetY, 0);
            }
            else
            {
                var animation = _compositor.CreateScalarKeyFrameAnimation();
                
                // Use CubicBezier for premium feel
                var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.23f, 1.0f), new Vector2(0.32f, 1.0f));
                
                animation.InsertKeyFrame(1.0f, (float)targetY, easing);
                animation.Duration = TimeSpan.FromMilliseconds(450);
                animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                
                indicatorVisual.StartAnimation("Offset.Y", animation);
            }
        }


        private void AnimateSidebar(double targetWidth)
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            
            var animContainer = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = targetWidth,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuinticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animContainer, CustomNavContainer);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animContainer, "Width");

            var animGlassW = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = targetWidth,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuinticEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animGlassW, GlassBorder);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animGlassW, "Width");

            sb.Children.Add(animContainer);
            sb.Children.Add(animGlassW);
            sb.Begin();

            // Toggle expansion state for buttons
            string state = targetWidth > 100 ? "Expanded" : "Collapsed";
            foreach (var child in NavButtonsStack.Children)
            {
                if (child is Control control)
                {
                    VisualStateManager.GoToState(control, state, true);
                }
            }
        }

        private void CustomNavContainer_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sidebarHideTimer.Stop();
            AnimateSidebar(220);
        }

        private void CustomNavContainer_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateSidebar(60);
            if (_isSidebarVisible)
            {
                _sidebarHideTimer.Start();
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                if (RootGrid.Opacity < 1.0) SetWindowOpacity(1.0);
            }
        }

        public void SetWindowOpacity(double opacity)
        {
            RootGrid.Opacity = opacity;
        }

        private void NavButtonsStack_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            // Allow manual dragging of the scrollviewer content
            NavScrollViewer.ChangeView(NavScrollViewer.HorizontalOffset - e.Delta.Translation.X, null, null, true);
        }


        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            // Reset Default State
            AppTitleBar.Visibility = Visibility.Visible;
            CustomNavContainer.Visibility = Visibility.Visible;

            // Sync Button Selection
            var activeBtn = NavButtonsStack.Children.OfType<RadioButton>()
                .FirstOrDefault(b => b.Tag != null && GetPageTypeFromTag(b.Tag.ToString()) == e.SourcePageType);
            
            if (activeBtn != null)
            {
                activeBtn.IsChecked = true;
                UpdatePillPosition(activeBtn);
            }

            if (ContentFrame.SourcePageType == typeof(PlayerPage))
            {
                AppTitleBar.Visibility = Visibility.Collapsed;
                CustomNavContainer.Visibility = Visibility.Collapsed;
            }
        }

        // Global Back Handler logic (Can be called from a Back Button if added, or keyboard shortcut)
        public void TryGoBack()
        {
            // Check if current page handles back request (e.g. Closing Search)
            if (ContentFrame.Content is MoviesPage moviesPage)
            {
                if (moviesPage.HandleBackRequest()) return;
            }
            else if (ContentFrame.Content is SeriesPage seriesPage)
            {
                if (seriesPage.HandleBackRequest()) return;
            }

            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        public void SetFullScreen(bool isFullScreen)
        {
            if (this.AppWindow != null)
            {
                if (isFullScreen)
                {
                    this.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                    CustomNavContainer.Visibility = Visibility.Collapsed;
                    AppTitleBar.Visibility = Visibility.Collapsed;
                }
                else
                {
                    this.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
                    if (ContentFrame.SourcePageType != typeof(PlayerPage))
                    {
                        CustomNavContainer.Visibility = Visibility.Visible;
                        AppTitleBar.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        [System.Obsolete("Replaced by PiPWindow")]
        public void SetCompactOverlay(bool isCompact) { }


        // ==========================================
        // GLOBAL DOWNLOAD MANAGER UI
        // ==========================================
        private void InitializeDownloadManager()
        {
            Services.DownloadManager.Instance.Initialize(this.DispatcherQueue);
            Services.DownloadManager.Instance.DownloadStarted += OnDownloadStarted;
            Services.DownloadManager.Instance.DownloadChanged += OnDownloadChanged;
        }

        private void OnDownloadStarted(Services.DownloadItem item)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                var card = CreateDownloadCard(item);
                
                // If Queued, add to bottom. If Downloading, add to top.
                if (item.Status == Services.DownloadStatus.Queued)
                {
                    GlobalNotificationPanel.Children.Add(card);
                }
                else
                {
                    GlobalNotificationPanel.Children.Insert(0, card);
                }
                
                // Show Popup when download starts
                GlobalDownloadPopup.Visibility = Visibility.Visible;
            });
        }

        private void OnDownloadChanged(Services.DownloadItem item)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                // Find existing card
                var card = GlobalNotificationPanel.Children.OfType<Grid>().FirstOrDefault(g => g.Tag == item);
                if (card != null)
                {
                    UpdateDownloadCard(card, item);

                    // Reorder: Move downloading items to top
                    if (item.Status == Services.DownloadStatus.Downloading)
                    {
                        try 
                        {
                            var index = GlobalNotificationPanel.Children.IndexOf(card);
                            if (index > 0)
                            {
                                GlobalNotificationPanel.Children.Move((uint)index, 0);
                            }
                        } catch { /* Fallback or ignore if move fails */ }
                    }
                    
                    if (item.Status == Services.DownloadStatus.Completed || item.Status == Services.DownloadStatus.Cancelled)
                    {
                        // Different delays: 5s for Success, 1s for Cancel
                        double delaySeconds = (item.Status == Services.DownloadStatus.Cancelled) ? 1.0 : 5.0;
                        
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
                        timer.Tick += (s, e) => 
                        {
                             timer.Stop();
                             
                             // Fate Out Animation
                             var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                             {
                                 From = 1.0,
                                 To = 0.0,
                                 Duration = new Duration(TimeSpan.FromMilliseconds(300))
                             };
                             Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, card);
                             Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
                             
                             var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                             sb.Children.Add(anim);
                             sb.Completed += (sender, args) => 
                             {
                                 GlobalNotificationPanel.Children.Remove(card);
                                 
                                 // Auto-close popup if empty
                                 if (GlobalNotificationPanel.Children.Count == 0)
                                 {
                                     GlobalDownloadPopup.Visibility = Visibility.Collapsed;
                                 }
                             };
                             sb.Begin();
                        };
                        timer.Start();
                    }
                }
            });
        }
        
        public void ToggleDownloads()
        {
            GlobalDownloadPopup.Visibility = GlobalDownloadPopup.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
        
        private void ClosePopup_Click(object sender, RoutedEventArgs e)
        {
            GlobalDownloadPopup.Visibility = Visibility.Collapsed;
        }

        private bool _allPaused = false;

        private void TogglePauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (_allPaused)
            {
                // Resume
                Services.DownloadManager.Instance.ResumeAll();
                _allPaused = false;
                PauseResumeIcon.Glyph = "\uE769"; // Pause Icon
                ToolTipService.SetToolTip(PauseResumeAllButton, "Hepsini Duraklat");
            }
            else
            {
                // Pause
                Services.DownloadManager.Instance.PauseAll();
                _allPaused = true;
                PauseResumeIcon.Glyph = "\uE768"; // Play Icon (Resume)
                ToolTipService.SetToolTip(PauseResumeAllButton, "Hepsini Devam Ettir");
            }
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            Services.DownloadManager.Instance.CancelAll();
            // Reset UI state
            _allPaused = false;
            PauseResumeIcon.Glyph = "\uE769";
            ToolTipService.SetToolTip(PauseResumeAllButton, "Hepsini Duraklat");
        }

        private Grid CreateDownloadCard(Services.DownloadItem item)
        {
            var card = new Grid
            {
                Tag = item,
                Width = 320,
                Padding = new Thickness(16),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(240, 20, 20, 20)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } }
            };

            // Title
            var titleBlock = new TextBlock
            {
                Text = item.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(card.Children.AddAndReturn(titleBlock), 0);

            // Progress Bar
            var progressBar = new ProgressBar
            {
                Name = "ProgressBar",
                Maximum = 100,
                Value = 0,
                Height = 4,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Crimson),
                Margin = new Thickness(0, 0, 0, 8)
            };
             Grid.SetRow(card.Children.AddAndReturn(progressBar), 1);

            // Actions & Status Row
            var actionRow = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            Grid.SetRow(card.Children.AddAndReturn(actionRow), 2);

            // Status Text
            var statusBlock = new TextBlock
            {
                Name = "StatusBlock",
                Text = item.StatusText,
                FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2
            };
            Grid.SetColumn(actionRow.Children.AddAndReturn(statusBlock), 0);

            // Buttons Container
            var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Grid.SetColumn(actionRow.Children.AddAndReturn(buttonsPanel), 1);

            // Pause/Resume Button
            var pauseBtn = new Button
            {
                Name = "PauseButton",
                Content = new FontIcon { Glyph = "\uE769", FontSize = 12 }, // Pause
                Style = (Style)Application.Current.Resources["GlassButtonStyle"],
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Tag = item
            };
            pauseBtn.Click += PauseResume_Click;
            buttonsPanel.Children.Add(pauseBtn);

            // Cancel Button
            var cancelBtn = new Button
            {
                 Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
                 Style = (Style)Application.Current.Resources["GlassButtonStyle"],
                 Width = 28,
                 Height = 28,
                 Padding = new Thickness(0),
                 Tag = item
            };
            cancelBtn.Click += Cancel_Click;
            buttonsPanel.Children.Add(cancelBtn);

            return card;
        }

        private void UpdateDownloadCard(Grid card, Services.DownloadItem item)
        {
             // Find Children using LINQ or similar simple traversal (Name-based lookup is hard inside Grid if not registered, so manual)
             var progressBar = card.Children.OfType<ProgressBar>().FirstOrDefault();
             if (progressBar != null) progressBar.Value = item.Progress;

             var actionRow = card.Children.OfType<Grid>().LastOrDefault(); // Last row
             if (actionRow != null)
             {
                 var statusBlock = actionRow.Children.OfType<TextBlock>().FirstOrDefault();
                 if (statusBlock != null) statusBlock.Text = item.StatusText;
                 
                 var buttonsPanel = actionRow.Children.OfType<StackPanel>().FirstOrDefault();
                 if (buttonsPanel != null)
                 {
                     var pauseBtn = buttonsPanel.Children.OfType<Button>().FirstOrDefault(b => b.Name == "PauseButton");
                     if (pauseBtn != null)
                     {
                         var icon = pauseBtn.Content as FontIcon;
                         if (item.Status == Services.DownloadStatus.Paused || item.Status == Services.DownloadStatus.Failed)
                         {
                             icon.Glyph = "\uE768"; // Play (Resume)
                             ToolTipService.SetToolTip(pauseBtn, "Devam Et");
                         }
                         else
                         {
                             icon.Glyph = "\uE769"; // Pause
                             ToolTipService.SetToolTip(pauseBtn, "Duraklat");
                         }
                         
                         // Show Pause button for Downloading, Paused OR Failed (to allow retry)
                         pauseBtn.Visibility = (item.Status == Services.DownloadStatus.Downloading || 
                                                item.Status == Services.DownloadStatus.Paused ||
                                                item.Status == Services.DownloadStatus.Failed) 
                             ? Visibility.Visible : Visibility.Collapsed;
                     }
                 }
             }
        }

        private void PauseResume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Services.DownloadItem item)
            {
                if (item.Status == Services.DownloadStatus.Downloading)
                {
                    Services.DownloadManager.Instance.PauseDownload(item);
                }
                else if (item.Status == Services.DownloadStatus.Paused || item.Status == Services.DownloadStatus.Failed)
                {
                    Services.DownloadManager.Instance.ResumeDownload(item);
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is Services.DownloadItem item)
            {
                Services.DownloadManager.Instance.CancelDownload(item);
                // Card removal is handled by OnDownloadChanged logic
            }
        }

        private Type? GetPageTypeFromTag(string tag)
        {
            return tag switch
            {
                "LoginPage" => typeof(LoginPage),
                "LiveTVPage" => typeof(LiveTVPage),
                "MoviesPage" => typeof(MoviesPage),
                "SeriesPage" => typeof(SeriesPage),
                "MultiPlayerPage" => typeof(MultiPlayerPage),
                "AddonsPage" => typeof(Pages.AddonsPage),
                "WatchlistPage" => typeof(WatchlistPage),
                "SettingsPage" => typeof(SettingsPage),
                _ => typeof(MoviesPage) // Default fallback
            };
        }

    }
    
    // Extensions helper just for this file to make fluent UI building easier without extra class
    public static class GridExtensions
    {
        public static T AddAndReturn<T>(this UIElementCollection collection, T element) where T : UIElement
        {
            collection.Add(element);
            return element;
        }
    }
}
