using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models;
using System.Linq;

namespace ModernIPTVPlayer.Services
{
    public class AuthService
    {
        private static AuthService? _instance;
        public static AuthService Instance => _instance ??= new AuthService();

        private AuthService() { }

        public List<Playlist> GetSavedPlaylists()
        {
            try
            {
                var json = AppSettings.PlaylistsJson;
                return JsonSerializer.Deserialize<List<Playlist>>(json) ?? new List<Playlist>();
            }
            catch
            {
                return new List<Playlist>();
            }
        }

        public void SavePlaylists(List<Playlist> playlists)
        {
            AppSettings.PlaylistsJson = JsonSerializer.Serialize(playlists);
        }

        public async Task<bool> CheckAutoLoginAsync()
        {
            var lastId = AppSettings.LastPlaylistId;
            if (lastId.HasValue)
            {
                var playlists = GetSavedPlaylists();
                var playlist = playlists.FirstOrDefault(p => p.Id == lastId.Value);
                if (playlist != null)
                {
                    return await LoginWithPlaylistAsync(playlist);
                }
            }
            return false;
        }

        public async Task<bool> LoginWithPlaylistAsync(Playlist p)
        {
            if (p.Type == PlaylistType.M3u)
            {
                // Simple M3U doesn't have a "login" per se, but we check if it's reachable and set CurrentLogin
                App.CurrentLogin = new LoginParams 
                { 
                    PlaylistUrl = p.Url,
                    MaxConnections = 1 // Default
                };
                AppSettings.LastPlaylistId = p.Id;
                return true;
            }
            else
            {
                string cleanHost = CleanHost(p.Host);
                string authUrl = $"{cleanHost}/player_api.php?username={p.Username}&password={p.Password}";
                string playlistUrl = $"{cleanHost}/get.php?username={p.Username}&password={p.Password}&type=m3u_plus&output=ts";

                try
                {
                    HttpResponseMessage response = await HttpHelper.Client.GetAsync(authUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (response.IsSuccessStatusCode)
                    {
                        string authJson = await response.Content.ReadAsStringAsync();
                        int maxCons = 1;
                        try
                        {
                            var authData = JsonSerializer.Deserialize<XtreamAuthResponse>(authJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (authData?.UserInfo != null)
                            {
                                maxCons = authData.UserInfo.MaxConnections;
                                p.ExpiryDate = authData.UserInfo.FormattedExpiryDate;
                                
                                // Update saved playlist with new expiry metadata if it changed
                                var playlists = GetSavedPlaylists();
                                var existing = playlists.FirstOrDefault(pl => pl.Id == p.Id);
                                if (existing != null)
                                {
                                    existing.ExpiryDate = p.ExpiryDate;
                                    SavePlaylists(playlists);
                                }
                            }
                        }
                        catch { /* Fallback */ }

                        App.CurrentLogin = new LoginParams 
                        { 
                            PlaylistUrl = playlistUrl,
                            Host = cleanHost,
                            Username = p.Username,
                            Password = p.Password,
                            MaxConnections = maxCons
                        };
                        AppSettings.LastPlaylistId = p.Id;
                        return true;
                    }
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        public string CleanHost(string rawHost)
        {
            if (string.IsNullOrEmpty(rawHost)) return "";
            string host = rawHost.Trim();
            if (!host.StartsWith("http")) host = "http://" + host;
            host = host.TrimEnd('/');
            if (host.EndsWith("/get.php")) host = host.Substring(0, host.Length - "/get.php".Length);
            if (host.EndsWith("/player_api.php")) host = host.Substring(0, host.Length - "/player_api.php".Length);
            return host.TrimEnd('/');
        }
    }
}
