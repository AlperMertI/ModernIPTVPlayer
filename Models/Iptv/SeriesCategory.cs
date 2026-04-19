using System.Collections.Generic;

namespace ModernIPTVPlayer.Models.Iptv
{
    public class SeriesCategory
    {
        public string CategoryName { get; set; } = "Genel";

        public string CategoryId { get; set; } = "0";

        // Bu alan JSON'dan gelmez, biz dolduracağız
        public IReadOnlyList<SeriesStream> Series { get; set; } = new List<SeriesStream>();

        public override string ToString() => CategoryName;
    }
}
