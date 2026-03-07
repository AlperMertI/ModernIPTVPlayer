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
            var tasks = items.Select(item => Services.Metadata.MetadataProvider.Instance.GetMetadataAsync(item)).ToList();
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
    }
}
