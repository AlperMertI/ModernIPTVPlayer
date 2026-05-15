using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ModernIPTVPlayer.ViewModels
{
    /// <summary>
    /// A robust, NativeAOT-compatible ViewModel for media information.
    /// Encapsulates all data required for the MediaInfoPage without UI-layer coupling.
    /// </summary>
    public class MediaInfoViewModel : INotifyPropertyChanged
    {
        private string _title;
        private string _superTitle;
        private string _overview;
        private string _rating;
        private string _year;
        private string _duration;
        private string _ageRating;
        private string _country;
        private string _resolution;
        private string _codec;
        private string _bitrate;
        private bool _is4K;
        private bool _isHDR;
        private string _logoUrl;
        private string _backdropUrl;

        public event PropertyChangedEventHandler PropertyChanged;

        public string Title { get => _title; set => SetField(ref _title, value); }
        public string SuperTitle { get => _superTitle; set => SetField(ref _superTitle, value); }
        public string Overview { get => _overview; set => SetField(ref _overview, value); }
        public string Rating { get => _rating; set => SetField(ref _rating, value); }
        public string Year { get => _year; set => SetField(ref _year, value); }
        public string Duration { get => _duration; set => SetField(ref _duration, value); }
        public string AgeRating { get => _ageRating; set => SetField(ref _ageRating, value); }
        public string Country { get => _country; set => SetField(ref _country, value); }
        public string Resolution { get => _resolution; set => SetField(ref _resolution, value); }
        public string Codec { get => _codec; set => SetField(ref _codec, value); }
        public string Bitrate { get => _bitrate; set => SetField(ref _bitrate, value); }
        public bool Is4K { get => _is4K; set => SetField(ref _is4K, value); }
        public bool IsHDR { get => _isHDR; set => SetField(ref _isHDR, value); }
        public string LogoUrl { get => _logoUrl; set => SetField(ref _logoUrl, value); }
        public string BackdropUrl { get => _backdropUrl; set => SetField(ref _backdropUrl, value); }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
