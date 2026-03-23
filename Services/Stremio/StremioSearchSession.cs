using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Services.Stremio
{
    public class StremioSearchSession
    {
        public string Query { get; }
        public List<StremioMediaStream> RawResults { get; } = new();
        public List<StremioMediaStream> RankedResults { get; private set; } = new();
        public bool IsCompleted { get; private set; }
        
        private readonly List<Action<List<StremioMediaStream>>> _subscribers = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _searchTask;
        private readonly object _lock = new();
        private DateTime _lastRankingTime = DateTime.MinValue;
        private const int RANKING_THROTTLE_MS = 600;

        public StremioSearchSession(string query, Func<string, Action<List<StremioMediaStream>>, CancellationToken, Task<List<StremioMediaStream>>> searchFunc)
        {
            Query = query;
            _searchTask = Task.Run(async () => 
            {
                try
                {
                    var finalResults = await searchFunc(query, OnPartialResults, _cts.Token);
                    lock (_lock)
                    {
                        RankedResults = finalResults;
                        IsCompleted = true;
                    }
                    NotifySubscribers(RankedResults);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.Error($"SearchSession error for '{query}'", ex);
                }
            });
        }

        public void Subscribe(Action<List<StremioMediaStream>> callback)
        {
            lock (_lock)
            {
                if (IsCompleted || RankedResults.Count > 0)
                {
                    callback(RankedResults);
                }
                _subscribers.Add(callback);
            }
        }

        public void Unsubscribe(Action<List<StremioMediaStream>> callback)
        {
            lock (_lock) { _subscribers.Remove(callback); }
        }

        public void Cancel()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        private void OnPartialResults(List<StremioMediaStream> ranked)
        {
            lock (_lock) { RankedResults = ranked; }
            NotifySubscribers(ranked);
        }

        private void NotifySubscribers(List<StremioMediaStream> results)
        {
            List<Action<List<StremioMediaStream>>> targets;
            lock (_lock) { targets = _subscribers.ToList(); }
            foreach (var sub in targets)
            {
                try { sub(results); } catch { }
            }
        }
    }
}
