using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models.Stremio
{
    public class StremioMediaStream : IMediaStream, INotifyPropertyChanged
    {
        public StremioMeta Meta { get; set; }
        
        public StremioMediaStream(StremioMeta meta)
        {
            Meta = meta;
        }

        public string SourceAddon { get; set; }

        // IMediaStream Implementation
        public int Id => Meta.Id.GetHashCode(); // Temporary Int ID for interface
        public string IMDbId => Meta.Id; // Real ID

        public string Title => Meta.Name;
        public string PosterUrl => Meta.Poster;
        public string Rating => Meta.ImdbRating;
        public string Type => Meta.Type?.ToUpper();
        public string StreamUrl { get; set; } = "";

        // UI Binding Implementation
        public double ProgressValue => 0;
        public bool ShowProgress => false;
        public string BadgeText => "";
        public bool ShowBadge => false;

        public TmdbMovieResult TmdbInfo { get; set; } // Can populate later if needed

        // Technical Metadata (Probe Results)
        public bool HasMetadata => !string.IsNullOrEmpty(Resolution);
        public bool IsProbing { get; set; }
        public bool? IsOnline { get; set; }
        public string Resolution { get; set; }
        public string Fps { get; set; }
        public string Codec { get; set; }
        public long Bitrate { get; set; }
        public bool IsHdr { get; set; }

        // Properties for UI Binding
        public string Year => Meta.ReleaseInfo;
        public string Banner => Meta.Background;
        
        // Helper
        public BitmapImage PosterBitmap => !string.IsNullOrEmpty(PosterUrl) ? new BitmapImage(new System.Uri(PosterUrl)) : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
