using System.Collections.ObjectModel;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class CatalogRowViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _catalogName;
        private bool _isLoading;
        private ObservableCollection<StremioMediaStream> _items = new();

        public string CatalogName { get => _catalogName; set { _catalogName = value; OnPropertyChanged(); } }
        public ObservableCollection<StremioMediaStream> Items { get => _items; set { _items = value; OnPropertyChanged(); } }
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }
        public string RowId { get; set; }
        public int SortIndex { get; set; }
        public string RowStyle { get; set; } = "Standard"; // Standard, Landscape, Spotlight

        // Pagination Context
        public string SourceUrl { get; set; }
        public string CatalogType { get; set; }
        public string CatalogId { get; set; }
        public string Extra { get; set; }
        public int Skip { get; set; } = 0;
        public bool HasMore { get => _hasMore; set { _hasMore = value; OnPropertyChanged(); } }
        private bool _hasMore = true;

        public bool IsHeaderInteractive => !string.IsNullOrEmpty(SourceUrl);
        
        private bool _isLoadingMore;
        public bool IsLoadingMore { get => _isLoadingMore; set { _isLoadingMore = value; OnPropertyChanged(); } }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
