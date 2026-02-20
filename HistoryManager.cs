using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace ModernIPTVPlayer
{
    public class HistoryItem
    {
        public string Id { get; set; } // Movie ID or SeriesID_EpisodeID
        public string Title { get; set; }
        public string StreamUrl { get; set; }
        public double Position { get; set; } // Seconds
        public double Duration { get; set; } // Seconds
        public DateTime Timestamp { get; set; }
        public bool IsFinished { get; set; } // > 95%
        
        public string SeriesName { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        // To track "Next Up", we might need to know the parent Series ID
        public string ParentSeriesId { get; set; }

        public string AudioTrackId { get; set; }
        public string SubtitleTrackId { get; set; }
        public string SubtitleTrackUrl { get; set; } // For Addon/External subs
    }

    public class HistoryManager
    {
        private static HistoryManager _instance;
        public static HistoryManager Instance => _instance ??= new HistoryManager();

        private Dictionary<string, HistoryItem> _history = new();
        private const string FILENAME = "watch_history.json";
        private bool _loaded = false;
        private readonly object _lock = new();

        private HistoryManager() { }

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
                    var list = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                    
                    lock (_lock)
                    {
                        _history = list.ToDictionary(x => x.Id, x => x);
                    }
                }
                _loaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryManager] Load Error: {ex.Message}");
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                List<HistoryItem> list;
                lock (_lock)
                {
                    list = _history.Values.OrderByDescending(x => x.Timestamp).Take(200).ToList(); // Keep last 200
                }

                var json = JsonSerializer.Serialize(list);
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(FILENAME, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
                
                System.Diagnostics.Debug.WriteLine($"[HistoryManager] Saved {list.Count} items to {file.Path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryManager] Save Error: {ex.Message}");
            }
        }

        public void UpdateProgress(string id, string title, string url, double pos, double dur, string parentId = null, string seriesName = null, int s = 0, int e = 0, string aid = null, string sid = null, string subUrl = null)
        {
            if (string.IsNullOrEmpty(id) || dur < 1) return;

            // Mark finished if > 95%
            bool finished = (pos / dur) > 0.95;

            lock (_lock)
            {
                if (!_history.ContainsKey(id))
                {
                    _history[id] = new HistoryItem { Id = id };
                }

                var item = _history[id];
                item.Title = title;
                item.StreamUrl = url;
                item.Position = pos;
                item.Duration = dur;
                item.Timestamp = DateTime.Now;
                item.IsFinished = finished;
                
                if (parentId != null)
                {
                    item.ParentSeriesId = parentId;
                    item.SeriesName = seriesName;
                    item.SeasonNumber = s;
                    item.EpisodeNumber = e;
                }

                if (!string.IsNullOrEmpty(aid)) item.AudioTrackId = aid;
                if (!string.IsNullOrEmpty(sid)) item.SubtitleTrackId = sid;
                if (!string.IsNullOrEmpty(subUrl)) item.SubtitleTrackUrl = subUrl;
            }
            // Fire and forget save (maybe debounce this in real app, but for now direct)
            // Actually better to save on Pause/Stop/Navigation, not every tick.
            // But we will call SaveAsync() manually from PlayerPage on PageUnload or Pause.
        }

        public HistoryItem GetProgress(string id)
        {
            lock (_lock)
            {
                return _history.ContainsKey(id) ? _history[id] : null;
            }
        }

        // Find the last watched episode for a series
        public HistoryItem GetLastWatchedEpisode(string seriesId)
        {
            lock (_lock)
            {
                // Find all history items for this series
                var historyItems = _history.Values
                    .Where(x => x.ParentSeriesId == seriesId)
                    .ToList();

                

                if (historyItems.Count == 0) return null;

                // 1. Get the absolute most recently accessed item
                var mostRecent = historyItems.OrderByDescending(x => x.Timestamp).FirstOrDefault();

                // 2. If it's unfinished, the user explicitly started it and left it halfway. Resume from here.
                if (mostRecent != null && !mostRecent.IsFinished)
                {
                    return mostRecent;
                }

                // 3. If it's finished, find the absolute "furthest progressed" episode in their entire history
                // (Highest Season, then Highest Episode) so the UI can auto-advance from the true leading edge.
                var furthestProgressed = historyItems
                    .OrderByDescending(x => x.SeasonNumber)
                    .ThenByDescending(x => x.EpisodeNumber)
                    .FirstOrDefault();


                var finalResult = furthestProgressed ?? mostRecent;
                return finalResult;
            }
        }
    }
}
