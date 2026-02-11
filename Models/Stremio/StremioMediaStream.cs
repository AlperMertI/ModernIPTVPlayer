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

        // IMediaStream Implementation
        public int Id => Meta.Id.GetHashCode(); // Temporary Int ID for interface
        public string IMDbId => Meta.Id; // Real ID

        public string Title => Meta.Name;
        public string PosterUrl => Meta.Poster;
        public string Rating => Meta.ImdbRating;

        public TmdbMovieResult TmdbInfo { get; set; } // Can populate later if needed

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
