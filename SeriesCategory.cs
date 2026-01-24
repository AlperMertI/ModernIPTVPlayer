using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer
{
    public class SeriesCategory
    {
        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; } = "Genel";

        [JsonPropertyName("category_id")]
        public string CategoryId { get; set; } = "0";

        // Bu alan JSON'dan gelmez, biz dolduracağız
        public List<SeriesStream> Series { get; set; } = new List<SeriesStream>();

        public override string ToString() => CategoryName;
    }
}
