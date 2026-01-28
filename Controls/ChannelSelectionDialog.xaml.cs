using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.UI.Xaml;

// Assuming LiveStream model is available in global namespace or we need to define a local DTO?
// Based on logs, LiveStream is likely in ModernIPTVPlayer namespace.

namespace ModernIPTVPlayer.Controls
{
    public sealed partial class ChannelSelectionDialog : ContentDialog
    {
        public LiveStream SelectedStream { get; private set; }
        
        private List<LiveStream> _allChannels = new();
        private List<LiveStream> _filteredChannels = new();
        private readonly HttpClient _httpClient = new HttpClient();

        public ChannelSelectionDialog()
        {
            this.InitializeComponent();
            
            // Set User-Agent to emulate a standard browser or player
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            
            this.Opened += ChannelSelectionDialog_Opened;
            this.Closing += ChannelSelectionDialog_Closing;
        }

        private async void ChannelSelectionDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            await LoadChannelsAsync();
        }

        private void ChannelSelectionDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (args.Result == ContentDialogResult.Primary)
            {
                if (ChannelList.SelectedItem is LiveStream stream)
                {
                    SelectedStream = stream;
                }
                else
                {
                    args.Cancel = true; // Force selection
                    StatusText.Text = "Lütfen bir kanal seçin.";
                    StatusText.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task LoadChannelsAsync()
        {
            try
            {
                LoadingBar.Visibility = Visibility.Visible;
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "Kanal Listesi İndiriliyor...";
                ChannelList.ItemsSource = null;

                var login = App.CurrentLogin;
                if (login == null)
                {
                    StatusText.Text = "Giriş bilgisi bulunamadı.";
                    LoadingBar.Visibility = Visibility.Collapsed;
                    return;
                }

                // Basit Xtream Fetch (LiveTVPage mantığıyla)
                // TODO: M3U desteği eklenmeli eğer login tipi M3U ise. Şimdilik Xtream varsayıyoruz.
                // Eğer playlistUrl doluysa m3u, değilse xtream.
                
                // LOGIC DECISION:
                // Prioritize XTREAM API if credentials explicitly exist. 
                // Only use PlaylistUrl (M3U) if we lack specific credentials or if explicitly in File Mode.
                
                bool hasXtreamCreds = !string.IsNullOrEmpty(login.Host) 
                                   && !string.IsNullOrEmpty(login.Username) 
                                   && !string.IsNullOrEmpty(login.Password);

                if (hasXtreamCreds)
                {
                    // 1. XTREAM API MODE
                    string baseUrl = login.Host;
                    if (baseUrl.EndsWith("/")) baseUrl = baseUrl.TrimEnd('/');
                    
                    string streamApi = $"{baseUrl}/player_api.php?username={login.Username}&password={login.Password}&action=get_live_streams";
                    
                    System.Diagnostics.Debug.WriteLine($"[ChannelDialog] Requesting Xtream: {streamApi}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, streamApi);
                    
                    var response = await _httpClient.SendAsync(request);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                         string errBody = "";
                         try { errBody = await response.Content.ReadAsStringAsync(); } catch {}
                         System.Diagnostics.Debug.WriteLine($"[ChannelDialog] Xtream Error {(int)response.StatusCode}: {errBody}");
                         throw new HttpRequestException($"Xtream API Error {(int)response.StatusCode} ({response.ReasonPhrase})");
                    }
                    
                    string json = await response.Content.ReadAsStringAsync();
                    
                    // JSON Deserialization
                    try 
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        _allChannels = JsonSerializer.Deserialize<List<LiveStream>>(json, options) ?? new List<LiveStream>();
                        
                        
                        foreach (var ch in _allChannels)
                        {
                            // Xtream format: http://host:port/live/username/password/stream_id.ts
                            ch.StreamUrl = $"{baseUrl}/live/{login.Username}/{login.Password}/{ch.StreamId}.ts";
                        }
                    }
                    catch (Exception jsonEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ChannelDialog] JSON Error: {jsonEx.Message}");
                        StatusText.Text = "Veri formatı hatası (JSON)";
                    }
                }
                else if (!string.IsNullOrEmpty(login.PlaylistUrl))
                {
                     // 2. M3U FALLBACK MODE
                     System.Diagnostics.Debug.WriteLine($"[ChannelDialog] Requesting M3U (Fallback): {login.PlaylistUrl}");
                     using var request = new HttpRequestMessage(HttpMethod.Get, login.PlaylistUrl);
                     
                     var response = await _httpClient.SendAsync(request);
                     
                     if (!response.IsSuccessStatusCode)
                     {
                         var err = await response.Content.ReadAsStringAsync();
                         System.Diagnostics.Debug.WriteLine($"[ChannelDialog] M3U Error {(int)response.StatusCode}: {err}");
                         throw new HttpRequestException($"M3U HTTP {(int)response.StatusCode}: {err}");
                     }
                     
                     string m3uContent = await response.Content.ReadAsStringAsync();
                     _allChannels = ParseM3uSimple(m3uContent);
                }
                else
                {
                     StatusText.Text = "Geçerli bir kaynak bulunamadı (Ne Xtream, Ne M3U).";
                }


                _filteredChannels = _allChannels; // Init
                ChannelList.ItemsSource = _filteredChannels;

                StatusText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Hata: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[ChannelDialog] {ex}");
            }
            finally
            {
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                string query = sender.Text.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(query))
                {
                    _filteredChannels = _allChannels;
                }
                else
                {
                    _filteredChannels = _allChannels
                        .Where(c => c.Name != null && c.Name.ToLowerInvariant().Contains(query))
                        .Take(100) // Limit results for speed
                        .ToList();
                }
                ChannelList.ItemsSource = _filteredChannels;
            }
        }

        // Simplified Helpers
        private List<LiveStream> ParseM3uSimple(string content)
        {
            // Simple robust parser for dialog
            var list = new List<LiveStream>();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string name = null;
            string icon = null;

            foreach (var line in lines)
            {
                var trim = line.Trim();
                if (trim.StartsWith("#EXTINF"))
                {
                    // Extract name (after comma)
                    var commaIndex = trim.LastIndexOf(',');
                    if (commaIndex != -1) name = trim.Substring(commaIndex + 1).Trim();
                    
                    // Extract logo
                    var logoMarker = "tvg-logo=\"";
                    var logoIdx = trim.IndexOf(logoMarker);
                    if (logoIdx != -1)
                    {
                        var start = logoIdx + logoMarker.Length;
                        var end = trim.IndexOf("\"", start);
                        if (end != -1) icon = trim.Substring(start, end - start);
                    }
                }
                else if (!trim.StartsWith("#") && !string.IsNullOrEmpty(trim))
                {
                    if (name != null)
                    {
                        list.Add(new LiveStream 
                        { 
                            Name = name, 
                            StreamUrl = trim, 
                            IconUrl = icon ?? "/Assets/def_station.png" 
                        });
                        name = null; icon = null;
                    }
                }
            }
            return list;
        }
    }
}
