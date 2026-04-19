using System.Collections.Generic;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioVideo
    {
        public string Id { get; set; } // "tt1234:1:1"

        private int _nameOff = -1, _nameLen = 0;
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; } 
        }

        private int _titleOff = -1, _titleLen = 0;
        public string Title 
        { 
            get => MetadataBuffer.GetString(_titleOff, _titleLen); 
            set { if (MetadataBuffer.IsEqual(_titleOff, _titleLen, value)) return; var r = MetadataBuffer.Store(value); _titleOff = r.Offset; _titleLen = r.Length; } 
        }

        private int _relOff = -1, _relLen = 0;
        public string Released 
        { 
            get => MetadataBuffer.GetString(_relOff, _relLen); 
            set { if (MetadataBuffer.IsEqual(_relOff, _relLen, value)) return; var r = MetadataBuffer.Store(value); _relOff = r.Offset; _relLen = r.Length; } 
        }

        private int _thumbOff = -1, _thumbLen = 0;
        public string Thumbnail 
        { 
            get => MetadataBuffer.GetString(_thumbOff, _thumbLen); 
            set { if (MetadataBuffer.IsEqual(_thumbOff, _thumbLen, value)) return; var r = MetadataBuffer.Store(value); _thumbOff = r.Offset; _thumbLen = r.Length; } 
        }

        private int _ratOff = -1, _ratLen = 0;
        public string Imdbrating 
        { 
            get => MetadataBuffer.GetString(_ratOff, _ratLen); 
            set { if (MetadataBuffer.IsEqual(_ratOff, _ratLen, value)) return; var r = MetadataBuffer.Store(value); _ratOff = r.Offset; _ratLen = r.Length; } 
        }

        public List<StremioStream> Streams { get; set; }

        public bool Available { get; set; }

        private int _runtimeOff = -1, _runtimeLen = 0;
        public string Runtime
        {
            get => MetadataBuffer.GetString(_runtimeOff, _runtimeLen);
            set { if (MetadataBuffer.IsEqual(_runtimeOff, _runtimeLen, value)) return; var r = MetadataBuffer.Store(value); _runtimeOff = r.Offset; _runtimeLen = r.Length; }
        }

        public int Episode { get; set; }

        public int Season { get; set; }

        private int _ovOff = -1, _ovLen = 0;
        public string Overview 
        { 
            get => MetadataBuffer.GetString(_ovOff, _ovLen); 
            set { if (MetadataBuffer.IsEqual(_ovOff, _ovLen, value)) return; var r = MetadataBuffer.Store(value); _ovOff = r.Offset; _ovLen = r.Length; } 
        }

        private int _descOff = -1, _descLen = 0;
        public string Description 
        { 
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; } 
        }
    }
}
