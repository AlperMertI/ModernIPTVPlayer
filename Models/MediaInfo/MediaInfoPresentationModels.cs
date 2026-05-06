using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// Cast presentation row used by the MediaInfo cast/director lists.
    /// </summary>
    [Microsoft.UI.Xaml.Data.Bindable]
    public class CastItem
    {
        public string Name { get; set; }
        public string Character { get; set; }
        public string FullProfileUrl { get; set; }
        public ImageSource ProfileImage { get; set; }
    }

    /// <summary>
    /// Stream row view model for source cards, including shimmer and active-source state.
    /// </summary>
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioStreamViewModel : INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string Name { get; set; }
        public string ProviderText { get; set; }
        public string AddonName { get; set; }
        public string AddonUrl { get; set; }
        public string Url { get; set; }
        public int? IptvStreamId { get; set; }
        public int? IptvSeriesId { get; set; }
        public string Externalurl { get; set; }
        public bool IsExternalLink => !string.IsNullOrEmpty(Externalurl) && string.IsNullOrEmpty(Url);

        private string _quality;
        public string Quality { get => _quality; set { if (_quality != value) { _quality = value; OnPropertyChanged(nameof(Quality)); OnPropertyChanged(nameof(HasQuality)); } } }
        public bool HasQuality => !string.IsNullOrEmpty(Quality);

        private string _size;
        public string Size { get => _size; set { if (_size != value) { _size = value; OnPropertyChanged(nameof(Size)); OnPropertyChanged(nameof(HasSize)); } } }
        public bool HasSize => !string.IsNullOrEmpty(Size);

        private bool _isHdr;
        public bool IsHdr { get => _isHdr; set { if (_isHdr != value) { _isHdr = value; OnPropertyChanged(nameof(IsHdr)); } } }

        private string _codec;
        public string Codec { get => _codec; set { if (_codec != value) { _codec = value; OnPropertyChanged(nameof(Codec)); OnPropertyChanged(nameof(HasCodec)); } } }
        public bool HasCodec => !string.IsNullOrEmpty(Codec);

        public bool IsCached { get; set; }
        public StremioStream OriginalStream { get; set; }

        private bool _isActive;
        public bool IsActive { get => _isActive; set { if (_isActive != value) { _isActive = value; OnPropertyChanged(nameof(IsActive)); } } }

        private bool _isPlaceholder;
        public bool IsPlaceholder { get => _isPlaceholder; set { if (_isPlaceholder != value) { _isPlaceholder = value; OnPropertyChanged(nameof(IsPlaceholder)); } } }

        private double _shimmerOpacity = 1.0;
        public double ShimmerOpacity { get => _shimmerOpacity; set { if (_shimmerOpacity != value) { _shimmerOpacity = value; OnPropertyChanged(nameof(ShimmerOpacity)); } } }

        public string SourceDisplayName => Title;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Addon tab view model for source selection.
    /// </summary>
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioAddonViewModel : INotifyPropertyChanged
    {
        private string _name;
        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } }
        public string AddonUrl { get; set; }

        private List<StremioStreamViewModel> _streams;
        public List<StremioStreamViewModel> Streams { get => _streams; set { if (_streams != value) { _streams = value; OnPropertyChanged(nameof(Streams)); } } }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; set { if (_isLoading != value) { _isLoading = value; OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(IsLoaded)); } } }
        public bool IsLoaded => !IsLoading;
        public int SortIndex { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
