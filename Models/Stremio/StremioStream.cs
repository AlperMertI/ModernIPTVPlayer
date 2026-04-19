using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models.Stremio
{
    public class StremioStreamResponse
    {
        public List<StremioStream> Streams { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioStream
    {
        public string Name { get; set; } // e.g., "Torrentio\n4K" or just "4K"

        public string Title { get; set; } // Details: "4K HDR 2.5GB"

        public string Description { get; set; }

        public string Url { get; set; } // DIRECT HTTP URL

        public string Externalurl { get; set; }

        public string Infohash { get; set; } // For Torrents (Magnet) - Not supported yet

        public int? Fileidx { get; set; } // For multi-file torrents
        
        public BehaviorHints Behaviorhints { get; set; }

        [JsonIgnore]
        public string AddonUrl { get; set; } // Internal tracking
    }
    
    public class BehaviorHints
    {
        public bool Notwebready { get; set; }
        
        public string Bingegroup { get; set; }
    }
}
