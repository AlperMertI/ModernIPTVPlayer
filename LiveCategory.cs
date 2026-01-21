using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer
{
    public class LiveCategory
    {
        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = "Genel";

        [JsonPropertyName("category_id")]
        public string CategoryId { get; set; } = "0";

        // Bu alan JSON'dan gelmez
        public List<LiveStream> Channels { get; set; } = new List<LiveStream>();

        // Varsayılan string gösterimi (UI için)
        public override string ToString() => CategoryName;
    }
}