using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class SpotlightInjectRow : UserControl
    {
        private WebView2 _webView;
        private List<StremioMediaStream> _items = new List<StremioMediaStream>();
        private int _currentIndex = 0;
        private bool _isTrailerPlaying = false;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");

        public event EventHandler<(IMediaStream Stream, UIElement SourceElement)> ItemClicked;
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

            // ScaleX animation for focus effect
            var scaleAnim = new DoubleAnimation
            {
                To = targetScale,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleAnim, ContainerTransform);
            Storyboard.SetTargetProperty(scaleAnim, "ScaleX");

            var sb = new Storyboard();
            sb.Children.Add(heightAnim);
            sb.Children.Add(scaleAnim);
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
        }

        private void SpotlightInjectRow_Unloaded(object sender, RoutedEventArgs e)
        {
            CleanupWebView();
        }

        private string _pendingTrailerId = null;
        private bool _isInViewport = false;

        private async void SpotlightInjectRow_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
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

        private async void SpotlightInjectRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (args.NewValue is CatalogRowViewModel vm && vm.Items != null && vm.Items.Count > 0)
            {
                // Take up to 5 items for the carousel
                _items = vm.Items.Take(5).ToList();
                _currentIndex = 0;
                
                if (_items.Count > 0)
                {
                    UpdateUI();
                    UpdateNavigationVisibility();
                    
                    // Pre-fetch metadata for all 5 items in the background
                    _ = PreFetchCarouselMetadataAsync(_items);
                    
                    await TryLoadTrailerAsync();
                }
            }
            else
            {
                CleanupWebView();
                _items.Clear();
                UpdateNavigationVisibility();
            }
        }

        private async Task PreFetchCarouselMetadataAsync(List<StremioMediaStream> items)
        {
            var tasks = items.Select(item => Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item, Services.Metadata.MetadataContext.Discovery)).ToList();
            await Task.WhenAll(tasks);
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

            TitleBlock.Text = item.Title ?? "";
            YearBlock.Text = item.Year ?? "";
            RatingBlock.Text = item.Rating ?? "";
            DescriptionBlock.Text = item.Meta?.Description ?? "";

            string imgUrl = !string.IsNullOrEmpty(item.Banner) ? item.Banner : item.PosterUrl;
            if (!string.IsNullOrEmpty(imgUrl))
            {
                FallbackImage.Source = new BitmapImage(new Uri(imgUrl));
            }
            
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
            if (_items.Count == 0 || _currentIndex >= _items.Count) return;
            var currentItem = _items[_currentIndex];

            string trailerId = null;

            // 1. Try existing trailers in meta
            if (currentItem.Meta?.Trailers != null && currentItem.Meta.Trailers.Count > 0)
            {
                trailerId = currentItem.Meta.Trailers.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Source))?.Source;
            }

            // 2. Try fetching from metadata provider (which should be cached or pre-fetched)
            if (string.IsNullOrEmpty(trailerId))
            {
                try
                {
                    var unified = await Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(currentItem);
                    if (currentItem != _items[_currentIndex]) return;

                    if (unified != null && !string.IsNullOrEmpty(unified.TrailerUrl))
                    {
                        trailerId = unified.TrailerUrl;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Spotlight] Meta fetch error: {ex.Message}");
                }
            }
            
            if (currentItem != _items[_currentIndex]) return;

            if (!string.IsNullOrEmpty(trailerId))
            {
                string ytId = ExtractYouTubeId(trailerId);
                if (string.IsNullOrEmpty(ytId)) return;

                _pendingTrailerId = ytId;
                
                if (_isInViewport)
                {
                    if (_webView == null)
                    {
                        InitializeWebView(ytId);
                    }
                    else if (_webView.CoreWebView2 != null)
                    {
                        // Optimization: Switch video within same WebView
                        await _webView.CoreWebView2.ExecuteScriptAsync($"switchVideo('{ytId}');");
                        UpdateMuteButtonIcon();
                    }
                    else 
                    {
                         // Wait slightly if initializing
                         _ = Task.Run(async () => {
                              await Task.Delay(500);
                              DispatcherQueue?.TryEnqueue(async () => {
                                  if (_webView?.CoreWebView2 != null && currentItem == _items[_currentIndex]) {
                                      await _webView.CoreWebView2.ExecuteScriptAsync($"switchVideo('{ytId}');");
                                      UpdateMuteButtonIcon();
                                  }
                              });
                         });
                    }
                }
            }
            else
            {
                _pendingTrailerId = null;
                // If it was playing, hide it or stop it?
                VideoContainer.Opacity = 0;
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
            catch { }

            return source;
        }

        private async void InitializeWebView(string ytId)
        {
            if (_webView != null || _isTrailerPlaying) return;
            
            try
            {
                _webView = new WebView2();
                _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                _webView.VerticalAlignment = VerticalAlignment.Stretch;
                
                VideoContainer.Children.Add(_webView);

                string envFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTVPlayer_Spotlight", "env_" + _instanceId);
                System.IO.Directory.CreateDirectory(envFolder);
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, envFolder, null);
                await _webView.EnsureCoreWebView2Async(env);
                
                string contentFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTVPlayer_Spotlight", "content_" + _instanceId);
                System.IO.Directory.CreateDirectory(contentFolder);

                string virtualHost = $"spotlight-{_instanceId}.moderniptv.local";
                
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
                videoId: '{ytId}', host: 'https://www.youtube-nocookie.com',
                playerVars: {{
                    autoplay: 1, mute: 1, controls: 0, disablekb: 1,
                    fs: 0, rel: 0, modestbranding: 1, showinfo: 0,
                    iv_load_policy: 3, playsinline: 1, loop: 1, playlist: '{ytId}'
                }},
                events: {{
                    'onReady': function(e) {{ e.target.mute(); e.target.playVideo(); window.chrome.webview.postMessage('READY'); }}
                }}
            }});
        }}

        function switchVideo(newId) {{
            if (player && player.loadVideoById) {{
                player.loadVideoById({{'videoId': newId, 'startSeconds': 0}});
                player.playVideo();
                window.chrome.webview.postMessage('READY');
            }}
        }}
    </script>
</body>
</html>";
                
                string htmlFilePath = System.IO.Path.Combine(contentFolder, "spotlight.html");
                await System.IO.File.WriteAllTextAsync(htmlFilePath, htmlContent);
                
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    virtualHost, contentFolder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.Source = new Uri($"https://{virtualHost}/spotlight.html");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spotlight] WebView Error: {ex.Message}");
            }
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (args.TryGetWebMessageAsString() == "READY")
            {
                _isTrailerPlaying = true;
                
                ExpandButton.Visibility = Visibility.Visible;
                MuteButton.Visibility = Visibility.Visible;
                UpdateMuteButtonIcon();

                var sb = new Storyboard();
                var anim = new DoubleAnimation { To = 0.8, Duration = TimeSpan.FromSeconds(2) }; 
                Storyboard.SetTarget(anim, VideoContainer);
                Storyboard.SetTargetProperty(anim, "Opacity");
                sb.Children.Add(anim);
                sb.Begin();
            }
        }

        private void CleanupWebView()
        {
            _pendingTrailerId = null;
            if (_webView != null)
            {
                try
                {
                    _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    _webView.Close();
                }
                catch { }
                
                VideoContainer.Children.Clear();
                _webView = null;
                _isTrailerPlaying = false;
                
                ExpandButton.Visibility = Visibility.Collapsed;
                MuteButton.Visibility = Visibility.Collapsed;

                try
                {
                    string basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernIPTVPlayer_Spotlight");
                    string envFolder = System.IO.Path.Combine(basePath, "env_" + _instanceId);
                    string contentFolder = System.IO.Path.Combine(basePath, "content_" + _instanceId);
                    if (System.IO.Directory.Exists(contentFolder))
                        System.IO.Directory.Delete(contentFolder, true);
                    if (System.IO.Directory.Exists(envFolder))
                        _ = Task.Run(async () => { await Task.Delay(2000); try { System.IO.Directory.Delete(envFolder, true); } catch { } });
                }
                catch { }
            }
            VideoContainer.Opacity = 0;
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count <= 1) return;
            
            _currentIndex--;
            if (_currentIndex < 0) _currentIndex = _items.Count - 1;
            
            UpdateUI();
            await TryLoadTrailerAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count <= 1) return;
            
            _currentIndex++;
            if (_currentIndex >= _items.Count) _currentIndex = 0;
            
            UpdateUI();
            await TryLoadTrailerAsync();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                var item = _items[_currentIndex];
                ElementSoundPlayer.Play(ElementSoundKind.Invoke);
                CleanupWebView();
                ItemClicked?.Invoke(this, (item, FallbackImage));
            }
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
                catch { }

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
            if (_webView != null && _webView.CoreWebView2 != null)
            {
                _isMuted = !_isMuted;
                string script = _isMuted ? "player.mute();" : "player.unMute();";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
                UpdateMuteButtonIcon();
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
            IsExpanded = !IsExpanded;
            ElementSoundPlayer.Play(ElementSoundKind.Invoke);
        }
    }
}
