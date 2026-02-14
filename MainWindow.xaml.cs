using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
        
        // Expose NavView for PiP mode access from PlayerPage
        public NavigationView NavigationViewControl => NavView;
        public UIElement TitleBarElement => AppTitleBar;
        public MainWindow()
        {
            Current = this;
            this.InitializeComponent();

            InitializeDownloadManager();
            
            // Set title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Default navigation
            NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
            ContentFrame.Navigate(typeof(LoginPage));

            NavView.Loaded += (s, e) => AnimateSidebarWaterfall();
            
            // Auto-Restore Opacity when Window is Activated (e.g. user clicks taskbar)
            this.Activated += MainWindow_Activated;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                // specific check: if opacity is 0, restore it
                if (RootGrid.Opacity < 1.0)
                {
                    SetWindowOpacity(1.0);
                }
            }
        }

        public void SetWindowOpacity(double opacity)
        {
            RootGrid.Opacity = opacity;
        }

        private void AnimateSidebarWaterfall()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(NavView).Compositor;
            var items = NavView.MenuItems.OfType<NavigationViewItem>().ToList();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var visual = ElementCompositionPreview.GetElementVisual(item);
                ElementCompositionPreview.SetIsTranslationEnabled(item, true);

                // Initial State
                visual.Opacity = 0;
                visual.Properties.InsertVector3("Translation", new Vector3(-20, 0, 0));

                // Slide Animation
                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(1f, new Vector3(0, 0, 0));
                slide.Duration = TimeSpan.FromMilliseconds(800);
                slide.DelayTime = TimeSpan.FromMilliseconds(i * 80);
                
                // Spring-like easing
                var cubic = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
                slide.IterationBehavior = AnimationIterationBehavior.Count;
                slide.IterationCount = 1;

                // Fade Animation
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1f, 1f);
                fade.Duration = TimeSpan.FromMilliseconds(600);
                fade.DelayTime = TimeSpan.FromMilliseconds(i * 80);

                visual.StartAnimation("Translation", slide);
                visual.StartAnimation("Opacity", fade);
            }
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(SettingsPage));
                return;
            }

            if (args.InvokedItemContainer?.Tag is string tag)
            {
                Type? pageType = tag switch
                {
                    "LoginPage" => typeof(LoginPage),
                    "LiveTVPage" => typeof(LiveTVPage),
                    "MoviesPage" => typeof(MoviesPage), 
                    "SeriesPage" => typeof(SeriesPage),
                    "MultiPlayerPage" => typeof(MultiPlayerPage),

                    "AddonsPage" => typeof(Pages.AddonsPage),
                    "WatchlistPage" => typeof(WatchlistPage),
                    _ => null
                };

                if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
                {
                    ContentFrame.Navigate(pageType, App.CurrentLogin);
                }
            }
        }

        private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
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

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            NavView.IsPaneVisible = true; // Default to visible, special cases below
            NavView.AlwaysShowHeader = false; // Default to hide header for minimalist look

            // Reset Default State
            AppTitleBar.Visibility = Visibility.Visible;
            ContentFrame.Margin = new Thickness(0, 4, 0, 0);

            // Make sure the header reflects the current page
            if (ContentFrame.SourcePageType == typeof(LoginPage))
            {
                NavView.Header = null;
                NavView.AlwaysShowHeader = false;
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "LoginPage");
            }
            else if (ContentFrame.SourcePageType == typeof(LiveTVPage))
            {
                NavView.Header = null; // Custom Header in Page
                NavView.AlwaysShowHeader = false; 
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "LiveTVPage");
            }
            else if (ContentFrame.SourcePageType == typeof(MoviesPage))
            {
                NavView.Header = "Filmler";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "MoviesPage");
            }
            else if (ContentFrame.SourcePageType == typeof(SeriesPage))
            {
                NavView.Header = "Diziler";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "SeriesPage");
            }
            else if (ContentFrame.SourcePageType == typeof(Pages.AddonsPage))
            {
                NavView.Header = "Eklentiler";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "AddonsPage");
            }
            else if (ContentFrame.SourcePageType == typeof(WatchlistPage))
            {
                NavView.Header = "İzleme Listesi";
                NavView.IsPaneVisible = true;
                NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(i => i.Tag.ToString() == "WatchlistPage");
            }
            else if (ContentFrame.SourcePageType == typeof(PlayerPage))
            {
                NavView.Header = null; // Hide header for player
                NavView.IsPaneVisible = false;
                NavView.AlwaysShowHeader = false;
                
                // Hide TitleBar and Remove Margins for Full Immersion
                AppTitleBar.Visibility = Visibility.Collapsed;
                ContentFrame.Margin = new Thickness(0);
            }
        }
        public void SetFullScreen(bool isFullScreen)
        {
            if (this.AppWindow != null)
            {
                if (isFullScreen)
                {
                    this.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
                    
                    // Immersive mode adjustments
                    NavView.IsPaneVisible = false;
                    AppTitleBar.Visibility = Visibility.Collapsed;
                    ContentFrame.Margin = new Thickness(0);
                }
                else
                {
                    this.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
                    
                    // Restore navigation & UI (Based on current page logic)
                    if (ContentFrame.SourcePageType != typeof(PlayerPage))
                    {
                        NavView.IsPaneVisible = true;
                        AppTitleBar.Visibility = Visibility.Visible;
                        ContentFrame.Margin = new Thickness(0, 4, 0, 0);
                    }
                    else
                    {
                        // PlayerPage default state
                        NavView.IsPaneVisible = false;
                        AppTitleBar.Visibility = Visibility.Collapsed; 
                        ContentFrame.Margin = new Thickness(0);
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
