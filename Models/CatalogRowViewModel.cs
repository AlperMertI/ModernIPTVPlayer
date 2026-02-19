using System.Collections.ObjectModel;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models
{
    public class CatalogRowViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _catalogName;
        private bool _isLoading;
        private ObservableCollection<StremioMediaStream> _items = new();

        public string CatalogName { get => _catalogName; set { _catalogName = value; OnPropertyChanged(); } }
        public ObservableCollection<StremioMediaStream> Items { get => _items; set { _items = value; OnPropertyChanged(); } }
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); } }
        public int SortIndex { get; set; }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
