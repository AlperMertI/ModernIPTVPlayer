using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ModernIPTVPlayer.Controls;
using ModernIPTVPlayer.Models;
using System;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;

namespace ModernIPTVPlayer
{
    public sealed partial class MediaInfoPage : Page
    {
        private IMediaStream _item;
        private Compositor _compositor;

        public MediaInfoPage()
        {
            this.InitializeComponent();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 1. Setup Parallax
            SetupParallax();

            // 2. Load Data
            if (e.Parameter is IMediaStream item)
            {
                _item = item;
                await LoadDetailsAsync(item);

                // Title animation removed as TitleTranslate no longer exists in XAML
            }
        }

        private void SetupParallax()
        {
            try
            {
                var scrollProperties = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(MainScrollViewer);
                var heroVisual = ElementCompositionPreview.GetElementVisual(HeroImage);
                var expression = _compositor.CreateExpressionAnimation("ScrollManipulation.Translation.Y * Multiplier");
                expression.SetScalarParameter("Multiplier", 0.5f); 
                expression.SetReferenceParameter("ScrollManipulation", scrollProperties);
                heroVisual.StartAnimation("Offset.Y", expression);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Parallax Setup Error: {ex.Message}");
            }
        }

        private async Task LoadDetailsAsync(IMediaStream item)
        {
            TitleText.Text = item.Title;
            
            // Default Poster
            if (!string.IsNullOrEmpty(item.PosterUrl))
            {
                var bmp = new BitmapImage(new Uri(item.PosterUrl));
                MainPoster.ImageUrl = item.PosterUrl;
                HeroImage.Source = bmp; // Low res default
                
                // Color Extraction for Theme
                var colors = await ImageHelper.GetOrExtractColorAsync(item.PosterUrl);
                if (colors != null)
                {
                    // Apply Tint
                    var primary = colors.Value.Primary;
                    var secondary = colors.Value.Secondary;
                    
                    // Character spacing and colors are handled via XAML now
                    
                    // Tint Play Button
                     PlayButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)); // Keep white for contrast
                     // Maybe tiny tint on hero opacity?
                }
            }

            // TMDB
            var tmdb = await TmdbHelper.SearchMovieAsync(item.Title);
            if (tmdb != null)
            {
                OverviewText.Text = tmdb.Overview;
                RatingText.Text = $"{tmdb.VoteAverage:F1} Match";
                YearText.Text = tmdb.ReleaseDate?.Split('-')[0] ?? "";
                
                if (!string.IsNullOrEmpty(tmdb.FullBackdropUrl))
                {
                    HeroImage.Source = new BitmapImage(new Uri(tmdb.FullBackdropUrl));
                }
                
                // Pre-buffer instant play if LiveStream
                if (item is LiveStream live)
                {
                     // Smart Pre-buffer logic
                     // We keep it Opacity=0 instead of Collapsed to allow Init
                     
                     // We want to load it paused.
                     // MPV wrapper provides SetPropertyAsync and OpenAsync directly.
                     
                     // 1. Initialize logic if needed. 
                     // MpvPlayer might need explicit init if not auto-init.
                     // Assuming it auto-inits or we call something? PlayerPage calls InitializePlayerAsync.
                     // We should try to call it.
                     
                     try 
                     {
                         // Wait for init?
                         // await MediaInfoPlayer.InitializePlayerAsync(); // Check if this exists?
                         // PlayerPage L522 calls InitializePlayerAsync().
                         // So we should calls it too.
                         // But InitializePlayerAsync is likely in MpvPlayer which we can't see source of.
                         // PlayerPage uses it, so it exists.
                         
                         // BUT, InitializePlayerAsync probably loads libmpv. 
                         // Check if we can just call OpenAsync directly. If not, call init.
                         
                         // Assuming OpenAsync handles it or we need to init once.
                         // Let's rely on standard OpenAsync but first Set Pause.
                         
                         // Note: If player is not initialized, SetProperty might fail.
                         // PlayerPage calls InitializePlayerAsync explicitly.
                         
                         // Using Task.Run to not block UI if init is heavy
                         _ = Task.Run(async () => 
                         {
                             try
                             {
                                 // We need Dispatcher for UI if Init touches UI (it might).
                                 // Safest to do on UI thread really.
                                 DispatcherQueue.TryEnqueue(async () => 
                                 {
                                     try
                                     {
                                         // Ensure Container is Visible but Hidden (Opacity 0) to allow SwapChain creation
                                         PlayerOverlayContainer.Visibility = Visibility.Visible;
                                         PlayerOverlayContainer.Opacity = 0;
                                         PlayerOverlayContainer.IsHitTestVisible = false;

                                         // Wait for Loaded if needed
                                         if (!MediaInfoPlayer.IsLoaded)
                                         {
                                             var tcs = new TaskCompletionSource<bool>();
                                             RoutedEventHandler handler = null;
                                             handler = (s, e) =>
                                             {
                                                 MediaInfoPlayer.Loaded -= handler;
                                                 tcs.TrySetResult(true);
                                             };
                                             MediaInfoPlayer.Loaded += handler;
                                             await tcs.Task;
                                         }
                                         
                                         // FORCE Template Application to ensure _renderControl is not null
                                         MediaInfoPlayer.ApplyTemplate();

                                         await MediaInfoPlayer.InitializePlayerAsync();
                                         
                                         // PRE-BUFFER LIMITS (Strict 10 Seconds)
                                         // Using 200MB to ensure we hit time limit first, allowing max speed download.
                                         await MediaInfoPlayer.SetPropertyAsync("cache", "yes");
                                         await MediaInfoPlayer.SetPropertyAsync("demuxer-readahead-secs", "10");
                                         await MediaInfoPlayer.SetPropertyAsync("demuxer-max-bytes", "200MiB"); 
                                         await MediaInfoPlayer.SetPropertyAsync("demuxer-max-back-bytes", "1MiB");
                                         await MediaInfoPlayer.SetPropertyAsync("force-window", "yes");

                                         // HEADERS & USER-AGENT (Critical for avoiding server throttling)
                                         string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                                         await MediaInfoPlayer.SetPropertyAsync("user-agent", userAgent);

                                         string headers = "Accept: */*\nConnection: keep-alive\nAccept-Language: en-US,en;q=0.9\n";
                                         try {
                                             var targetUri = new Uri(live.StreamUrl);
                                             var cookies = HttpHelper.CookieContainer.GetCookies(targetUri);
                                             if (cookies.Count > 0) {
                                                 string cookieHeader = "";
                                                 foreach (System.Net.Cookie c in cookies) cookieHeader += $"{c.Name}={c.Value}; ";
                                                 headers += $"Cookie: {cookieHeader}\n";
                                             }
                                         } catch {}
                                         await MediaInfoPlayer.SetPropertyAsync("http-header-fields", headers);

                                         await MediaInfoPlayer.SetPropertyAsync("pause", "yes");
                                         await MediaInfoPlayer.OpenAsync(live.StreamUrl);
                                     }
                                     catch (Exception ex)
                                     {
                                         System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Pre-buffer Failed: {ex.Message}");
                                         // Hide it back if failed
                                         PlayerOverlayContainer.Visibility = Visibility.Collapsed;
                                     }
                                 });
                             }
                             catch(Exception ex)
                             {
                                 System.Diagnostics.Debug.WriteLine("Pre-buffer error: " + ex.Message);
                             }
                         });
                         
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine("MPV Init Error: " + ex.Message);
                     }
                }
            }
            else
            {
                OverviewText.Text = "No description available.";
            }
            
            // Logic for Series
            if (item is SeriesStream series)
            {
                SeasonsContainer.Visibility = Visibility.Visible;
                PlayButtonText.Text = "Play Ep 1";
                await LoadEpisodesAsync(series);
            }
            else
            {
                SeasonsContainer.Visibility = Visibility.Collapsed;
                PlayButtonText.Text = "Play Movie";
            }
        }

        private async Task LoadEpisodesAsync(SeriesStream series)
        {
             try
             {
                 // Retrieve credentials from AppSettings to make API call
                 var playlistsJson = AppSettings.PlaylistsJson;
                 var lastId = AppSettings.LastPlaylistId;
                 
                 if (string.IsNullOrEmpty(playlistsJson) || lastId == null) return;

                 var playlists = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Playlist>>(playlistsJson);
                 var activePlaylist = playlists?.Find(p => p.Id == lastId);

                 if (activePlaylist == null || string.IsNullOrEmpty(activePlaylist.Host)) return;

                 string baseUrl = activePlaylist.Host.TrimEnd('/');
                 string api = $"{baseUrl}/player_api.php?username={activePlaylist.Username}&password={activePlaylist.Password}&action=get_series_info&series_id={series.SeriesId}";
                 
                 var httpClient = HttpHelper.Client;
                 string json = await httpClient.GetStringAsync(api);
                 
                 if (string.IsNullOrEmpty(json)) return;

                 // Parse complex JSON: { "episodes": { "1": [...], "2": [...] }, "info": {...} }
                 // Since it's dynamic keys for seasons, we use JsonDocument
                 using (var doc = System.Text.Json.JsonDocument.Parse(json))
                 {
                     var root = doc.RootElement;
                     if (root.TryGetProperty("episodes", out var episodesNode))
                     {
                         var allEpisodes = new System.Collections.ObjectModel.ObservableCollection<EpisodeItem>();
                         
                         // Iterate through season properties "1", "2", etc.
                         foreach (var seasonProp in episodesNode.EnumerateObject())
                         {
                             // Each property is a season array
                             foreach(var ep in seasonProp.Value.EnumerateArray())
                             {
                                 string title = ep.GetProperty("title").GetString();
                                 string container = ep.GetProperty("container_extension").GetString();
                                 string id = ep.GetProperty("id").GetString(); // Stream ID for episode
                                 // We need full stream URL for playback: 
                                 // http://host:port/series/user/pass/{id}.{container}
                                 
                                 string finalUrl = $"{baseUrl}/series/{activePlaylist.Username}/{activePlaylist.Password}/{id}.{container}";
                                 string thumb = null;
                                 if (ep.TryGetProperty("info", out var info) && info.TryGetProperty("movie_image", out var img))
                                     thumb = img.GetString();
                                     
                                 // Fallback thumb
                                 if (string.IsNullOrEmpty(thumb)) thumb = series.Cover ?? _item.PosterUrl;

                                 allEpisodes.Add(new EpisodeItem 
                                 { 
                                     Title = title, // Usually "S1 E1" etc is not in title, we might want to prepend
                                     Duration = "24m", // No duration in simple api often
                                     ImageUrl = thumb,
                                     StreamUrl = finalUrl
                                 });
                             }
                         }
                         
                         EpisodesListView.ItemsSource = allEpisodes;
                         
                         // Update Play Button to play first episode
                         if (allEpisodes.Count > 0)
                         {
                             _firstEpisodeUrl = allEpisodes[0].StreamUrl;
                         }
                     }
                 }
             }
             catch(Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine("Episodes Error: " + ex.Message);
             }
        }
        
        private string _firstEpisodeUrl;

        private void EpisodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is EpisodeItem ep)
            {
                 PerformHandoverAndNavigate(ep.StreamUrl, ep.Title);
                 EpisodesListView.SelectedItem = null; // Reset selection
            }
        }

        #region Buttons

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
             if (_item is LiveStream stream)
            {
                PerformHandoverAndNavigate(stream.StreamUrl, stream.Name);
            }
            else if (_item is SeriesStream series && !string.IsNullOrEmpty(_firstEpisodeUrl))
            {
                 PerformHandoverAndNavigate(_firstEpisodeUrl, series.Name + " - Ep 1");
            }
        }

        private void PerformHandoverAndNavigate(string url, string title)
        {
            try
            {
                // 1. Prepare for Handover
                App.HandoffPlayer = MediaInfoPlayer;
                MediaInfoPlayer.EnableHandoffMode();
                
                // 2. Detach from current visual tree (Critical for reparenting)
                // Using parent casting to be safe if I moved it around
                if (MediaInfoPlayer.Parent is Panel parent)
                {
                    parent.Children.Remove(MediaInfoPlayer);
                }
                
                // 3. Navigate
                Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title));
            }
            catch (Exception ex)
            {
               System.Diagnostics.Debug.WriteLine($"Handover Error: {ex.Message}");
               // Fallback
               Frame.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(url, title));
            }
        }

        private void TrailerButton_Click(object sender, RoutedEventArgs e)
        {
            // Trailer logic placeholder
        }
        
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            // Favorites logic placeholder
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            // Normal Back
            if (Frame.CanGoBack) Frame.GoBack();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            
            // If we are navigating away AND we did NOT handoff the player, we must clean it up.
            if (MediaInfoPlayer != null && App.HandoffPlayer != MediaInfoPlayer) 
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Cleaning up unused player (Handoff: {App.HandoffPlayer != null})");
                
                // Stop playback to save bandwidth
                 _ = MediaInfoPlayer.ExecuteCommandAsync("stop");
                 
                 // IMPORTANT: Disable Handoff mode just in case it was enabled but then user navigated back
                 MediaInfoPlayer.DisableHandoffMode();

                 // Cleanup resources since Page is likely being destroyed
                 _ = MediaInfoPlayer.CleanupAsync();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MediaInfoPage] Handoff Active - Skipping Cleanup");
            }
        }

        #endregion
    }
    
    public class EpisodeItem
    {
        public string Title { get; set; }
        public string Duration { get; set; }
        public string ImageUrl { get; set; }
        public string StreamUrl { get; set; }
    }
}
