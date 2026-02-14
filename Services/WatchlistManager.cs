using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernIPTVPlayer.Services
{
    public class WatchlistManager
    {
        private static WatchlistManager _instance;
        public static WatchlistManager Instance => _instance ??= new WatchlistManager();

        private List<WatchlistItem> _watchlist = new();
        private const string FILENAME = "watchlist.json";
        private bool _loaded = false;
        private readonly object _lock = new();

        public event EventHandler WatchlistChanged;

        private WatchlistManager() { }

        public async Task InitializeAsync()
        {
            if (_loaded) return;
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(FILENAME);
                if (item != null)
                {
                    var file = await folder.GetFileAsync(FILENAME);
                    var json = await FileIO.ReadTextAsync(file);
                    var list = JsonSerializer.Deserialize<List<WatchlistItem>>(json);
                    
                    lock (_lock)
                    {
                         _watchlist = list ?? new List<WatchlistItem>();
                    }
                }
                _loaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WatchlistManager] Load Error: {ex.Message}");
            }
        }

        private async Task SaveAsync()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_watchlist);
                }

                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
                
                WatchlistChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WatchlistManager] Save Error: {ex.Message}");
            }
        }

        public async Task AddToWatchlist(IMediaStream stream)
        {
            if (stream == null) return;
            
            // Check if already exists
            if (IsOnWatchlist(stream)) return;

            var item = new WatchlistItem(stream);
            item.DateAdded = DateTime.Now;
            
            lock (_lock)
            {
                _watchlist.Insert(0, item); // Add to top
            }
            
            await SaveAsync();
        }

        public async Task RemoveFromWatchlist(IMediaStream stream)
        {
             if (stream == null) return;
             
             string id = GetId(stream);
             if (string.IsNullOrEmpty(id)) return;

             bool removed = false;
             lock (_lock)
             {
                 var item = _watchlist.FirstOrDefault(x => x.Id == id);
                 if (item != null)
                 {
                     _watchlist.Remove(item);
                     removed = true;
                 }
             }

             if (removed) await SaveAsync();
        }
        
        public async Task RemoveFromWatchlist(string id)
        {
             bool removed = false;
             lock (_lock)
             {
                 var item = _watchlist.FirstOrDefault(x => x.Id == id);
                 if (item != null)
                 {
                     _watchlist.Remove(item);
                     removed = true;
                 }
             }

             if (removed) await SaveAsync();
        }

        public bool IsOnWatchlist(IMediaStream stream)
        {
            if (stream == null) return false;
            string id = GetId(stream);
            if (string.IsNullOrEmpty(id)) return false;

            lock (_lock)
            {
                return _watchlist.Any(x => x.Id == id);
            }
        }
        
        // Helper to extract ID consistently
        private string GetId(IMediaStream stream)
        {
             if (stream is WatchlistItem w) return w.Id;
             if (stream is StremioMediaStream s) return s.IMDbId;
             if (stream is SeriesStream ss) return ss.SeriesId.ToString();
             if (stream is LiveStream l) return l.StreamId.ToString();
             return null;
        }

        public List<WatchlistItem> GetWatchlist()
        {
            lock (_lock)
            {
                return new List<WatchlistItem>(_watchlist);
            }
        }
        
        // Returns generic IMediaStream list for UI binding
        public List<IMediaStream> GetWatchlistAsMediaStreams()
        {
             lock (_lock)
             {
                 // Convert items back to StremioStreams if possible for rich metadata, 
                 // or return as WatchlistItem (which implements IMediaStream)
                 return _watchlist.Select(w => 
                 {
                     if (w.StremioMeta != null) return (IMediaStream)w.ToStremioStream();
                     return (IMediaStream)w;
                 }).ToList();
             }
        }
    }
}
