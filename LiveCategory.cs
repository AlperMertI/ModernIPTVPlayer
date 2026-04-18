using ModernIPTVPlayer.Models.Metadata;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class LiveCategory
    {
        private Helpers.BinaryCacheSession? _session;
        private int _nameOff, _nameLen;
        private int _idOff, _idLen;

        [JsonPropertyName("category_name")]
        public string CategoryName 
        { 
            get => _session != null ? _session.GetString(_nameOff, _nameLen) : _categoryName;
            set 
            {
                if (_session != null) { _session.PokeString(_nameOff, _nameLen, value); }
                _categoryName = value;
            }
        }
        private string _categoryName = "Genel";

        [JsonPropertyName("category_id")]
        public string CategoryId 
        { 
            get => _session != null ? _session.GetString(_idOff, _idLen) : _categoryId;
            set 
            {
                if (_session != null) { _session.PokeString(_idOff, _idLen, value); }
                _categoryId = value;
            }
        }
        private string _categoryId = "0";

        public void SetCacheSession(Helpers.BinaryCacheSession session) => _session = session;

        public void LoadFromRecord(CategoryRecord record)
        {
            _idOff = record.IdOff; _idLen = record.IdLen;
            _nameOff = record.NameOff; _nameLen = record.NameLen;
        }

        // Bu alan JSON'dan gelmez
        public IReadOnlyList<LiveStream> Channels { get; set; } = new List<LiveStream>();

        // Varsayılan string gösterimi (UI için)
        public override string ToString() => CategoryName;
    }
}