using System.Collections.Generic;
using System.Text.Json.Serialization;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioMetaResponse
    {
        public StremioMeta Meta { get; set; }
        
        public List<StremioMeta> Metas { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioMeta
    {
        public string Id { get; set; } // IMDB ID usually (tt1234567)

        public string Type { get; set; }

        public string Name { get; set; }

        public string Originalname { get; set; }

        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Aliases { get; set; }

        private int _posterOff, _posterLen;
        public string Poster 
        { 
            get => MetadataBuffer.GetString(_posterOff, _posterLen); 
            set { if (MetadataBuffer.IsEqual(_posterOff, _posterLen, value)) return; var r = MetadataBuffer.Store(value); _posterOff = r.Offset; _posterLen = r.Length; } 
        }

        private int _bgOff, _bgLen;
        public string Background 
        { 
            get => MetadataBuffer.GetString(_bgOff, _bgLen); 
            set { if (MetadataBuffer.IsEqual(_bgOff, _bgLen, value)) return; var r = MetadataBuffer.Store(value); _bgOff = r.Offset; _bgLen = r.Length; } 
        }

        private int _logoOff, _logoLen;
        public string Logo 
        { 
            get => MetadataBuffer.GetString(_logoOff, _logoLen); 
            set { if (MetadataBuffer.IsEqual(_logoOff, _logoLen, value)) return; var r = MetadataBuffer.Store(value); _logoOff = r.Offset; _logoLen = r.Length; } 
        }

        private int _descOff, _descLen;
        public string Description 
        { 
            get => MetadataBuffer.GetString(_descOff, _descLen); 
            set { if (MetadataBuffer.IsEqual(_descOff, _descLen, value)) return; var r = MetadataBuffer.Store(value); _descOff = r.Offset; _descLen = r.Length; } 
        }

        private int _relOff, _relLen;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Releaseinfo 
        { 
            get => MetadataBuffer.GetString(_relOff, _relLen); 
            set { if (MetadataBuffer.IsEqual(_relOff, _relLen, value)) return; var r = MetadataBuffer.Store(value); _relOff = r.Offset; _relLen = r.Length; } 
        }

        private int _yearOff, _yearLen;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Year 
        { 
            get => MetadataBuffer.GetString(_yearOff, _yearLen); 
            set { if (MetadataBuffer.IsEqual(_yearOff, _yearLen, value)) return; var r = MetadataBuffer.Store(value); _yearOff = r.Offset; _yearLen = r.Length; } 
        }

        private int _resOff, _resLen;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Released 
        { 
            get => MetadataBuffer.GetString(_resOff, _resLen); 
            set { if (MetadataBuffer.IsEqual(_resOff, _resLen, value)) return; var r = MetadataBuffer.Store(value); _resOff = r.Offset; _resLen = r.Length; } 
        }

        private int _ratOff, _ratLen;
        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? Imdbrating 
        { 
            get => MetadataBuffer.GetString(_ratOff, _ratLen); 
            set { if (MetadataBuffer.IsEqual(_ratOff, _ratLen, value)) return; var r = MetadataBuffer.Store(value); _ratOff = r.Offset; _ratLen = r.Length; } 
        }

        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Genres { get; set; }

        private int _runOff, _runLen;
        public string Runtime 
        { 
            get => MetadataBuffer.GetString(_runOff, _runLen); 
            set { if (MetadataBuffer.IsEqual(_runOff, _runLen, value)) return; var r = MetadataBuffer.Store(value); _runOff = r.Offset; _runLen = r.Length; } 
        }
        
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Cast { get; set; }
        
        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Director { get; set; }

        public List<StremioVideo> Videos { get; set; }

        public List<StremioMetaTrailer> Trailers { get; set; }

        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? MoviedbId { get; set; }

        public string ImdbId { get; set; }

        [JsonConverter(typeof(Helpers.UniversalStringConverter))]
        public string? TvdbId { get; set; }

        private int _webOff, _webLen;
        public string Website 
        { 
            get => MetadataBuffer.GetString(_webOff, _webLen); 
            set { if (MetadataBuffer.IsEqual(_webOff, _webLen, value)) return; var r = MetadataBuffer.Store(value); _webOff = r.Offset; _webLen = r.Length; } 
        }

        public List<StremioLink> Links { get; set; }

        public List<StremioTrailerStream> TrailerStreams { get; set; }

        public List<StremioCreditCast> CreditsCast { get; set; }

        public List<StremioCreditCrew> CreditsCrew { get; set; }

        public string Country { get; set; }

        [JsonConverter(typeof(Helpers.UniversalStringListConverter))]
        public List<string> Writer { get; set; }

        public string Status { get; set; }

        public StremioAppExtras AppExtras { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioAppExtras
    {
        public List<StremioAppCast> Cast { get; set; }

        public List<StremioAppCast> Directors { get; set; }

        public List<StremioAppCast> Writers { get; set; }

        public string Logo { get; set; }

        public string Trailer { get; set; }

        public List<StremioAppBackdrop> Backdrops { get; set; }

        public List<string> SeasonPosters { get; set; }

        public string Certification { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioAppBackdrop
    {
        public string Url { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioAppCast
    {
        public string Name { get; set; }

        public string Character { get; set; }

        public string Photo { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioCreditCast
    {
        public System.Text.Json.JsonElement Id { get; set; }

        public string Name { get; set; }

        public string Character { get; set; }

        public string ProfilePath { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioCreditCrew
    {
        public System.Text.Json.JsonElement Id { get; set; }

        public string Name { get; set; }

        public string Department { get; set; }

        public string Job { get; set; }

        public string ProfilePath { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioTrailerStream
    {
        public string Title { get; set; }

        public string YtId { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioLink
    {
        private int _nameOff, _nameLen;
        public string Name 
        { 
            get => MetadataBuffer.GetString(_nameOff, _nameLen); 
            set { if (MetadataBuffer.IsEqual(_nameOff, _nameLen, value)) return; var r = MetadataBuffer.Store(value); _nameOff = r.Offset; _nameLen = r.Length; } 
        }

        private int _catOff, _catLen;
        public string Category 
        { 
            get => MetadataBuffer.GetString(_catOff, _catLen); 
            set { if (MetadataBuffer.IsEqual(_catOff, _catLen, value)) return; var r = MetadataBuffer.Store(value); _catOff = r.Offset; _catLen = r.Length; } 
        }

        private int _urlOff, _urlLen;
        public string Url 
        { 
            get => MetadataBuffer.GetString(_urlOff, _urlLen); 
            set { if (MetadataBuffer.IsEqual(_urlOff, _urlLen, value)) return; var r = MetadataBuffer.Store(value); _urlOff = r.Offset; _urlLen = r.Length; } 
        }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioMetaTrailer
    {
        public string Source { get; set; } // YouTube ID

        public string Type { get; set; }
    }
}
