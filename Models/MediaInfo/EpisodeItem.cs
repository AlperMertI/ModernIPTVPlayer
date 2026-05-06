using System;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Episode presentation model for the detail page. It includes selection,
    /// watch-progress, and shimmer placeholder state used by ItemsRepeater templates.
    /// </summary>
    [Microsoft.UI.Xaml.Data.Bindable]
    public class EpisodeItem : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Name { get; set; }
        public string Overview { get; set; }
        public string Duration { get; set; }
        public string ImageUrl { get; set; }
        public ImageSource Thumbnail { get; set; }
        public string StreamUrl { get; set; }
        public string Container { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public int IptvSeriesId { get; set; }
        public int? IptvStreamId { get; set; }
        public string IptvSourceTitle { get; set; }
        public string Resolution { get; set; }
        public string VideoCodec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }
        public string EpisodeNumberFormatted => $"{EpisodeNumber}. Bölüm";
        public string DurationFormatted { get; set; }
        public bool IsReleased { get; set; } = true;
        public DateTime? ReleaseDate { get; set; }
        public string ReleaseDateFormatted => ReleaseDate.HasValue ? ReleaseDate.Value.ToString("d MMMM yyyy", new CultureInfo("tr-TR")) : "";

        private bool _isWatched;
        public bool IsWatched { get => _isWatched; set { if (_isWatched != value) { _isWatched = value; OnPropertyChanged(nameof(IsWatched)); } } }

        private bool _hasProgress;
        public bool HasProgress { get => _hasProgress; set { if (_hasProgress != value) { _hasProgress = value; OnPropertyChanged(nameof(HasProgress)); } } }

        private double _progressPercent;
        public double ProgressPercent { get => _progressPercent; set { if (Math.Abs(_progressPercent - value) > 0.01) { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); } } }

        public string ProgressText { get; set; }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } } }

        private bool _isPlaceholder;
        public bool IsPlaceholder { get => _isPlaceholder; set { if (_isPlaceholder != value) { _isPlaceholder = value; OnPropertyChanged(nameof(IsPlaceholder)); } } }

        private double _shimmerOpacity = 1.0;
        public double ShimmerOpacity { get => _shimmerOpacity; set { if (_shimmerOpacity != value) { _shimmerOpacity = value; OnPropertyChanged(nameof(ShimmerOpacity)); } } }

        public void RefreshHistoryState()
        {
            var history = HistoryManager.Instance.GetProgress(Id);
            UpdateProgress(history);
        }

        public void UpdateProgress(HistoryItem history)
        {
            if (history == null)
            {
                IsWatched = false; HasProgress = false; ProgressPercent = 0; ProgressText = "";
            }
            else
            {
                IsWatched = history.IsFinished;
                HasProgress = history.Position > 0 && !history.IsFinished;
                if (history.Duration > 0)
                {
                    ProgressPercent = (history.Position / history.Duration) * 100;
                    if (ProgressPercent > 98) { IsWatched = true; HasProgress = false; }
                    if (HasProgress)
                    {
                        var remaining = TimeSpan.FromSeconds(history.Duration - history.Position);
                        ProgressText = remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}sa {(int)remaining.Minutes}dk Kaldı" : $"{(int)remaining.TotalMinutes}dk Kaldı";
                    }
                }
                OnPropertyChanged(nameof(IsWatched));
                OnPropertyChanged(nameof(HasProgress));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
