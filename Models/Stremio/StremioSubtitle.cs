using System.Collections.Generic;

namespace ModernIPTVPlayer.Models.Stremio
{
    public class StremioSubtitleResponse
    {
        public List<StremioSubtitle> Subtitles { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class StremioSubtitle
    {
        public string Id { get; set; }

        public string Url { get; set; }

        public string Lang { get; set; }
    }
}
