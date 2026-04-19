using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models.Stremio
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioCatalog
    {
        public string Type { get; set; } // "movie", "series"

        public string Id { get; set; } // "top", "year"

        public string Name { get; set; }

        public List<StremioCatalogExtra> Extra { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioCatalogExtra
    {
        public string Name { get; set; } // "search", "genre"

        public bool Isrequired { get; set; }

        public List<string> Options { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioCatalogRoot
    {
        public List<StremioMeta> Metas { get; set; }
    }
}
