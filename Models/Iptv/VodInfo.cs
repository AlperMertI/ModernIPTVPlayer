using System;
using System.Collections.Generic;

namespace ModernIPTVPlayer.Models.Iptv
{
    public class VodInfoResponse
    {
        public VodInfo Info { get; set; }
        
        public VodStreamInfo Movie_data { get; set; }
    }

    public class VodInfo
    {
        public string Name { get; set; }
        
        public string Description { get; set; }
        
        public string Director { get; set; }
        
        public string Cast { get; set; }
        
        public string Rating { get; set; }
        
        public string Releasedate { get; set; }
        
        public string[] BackdropPath { get; set; }
        
        public string Genre { get; set; }

        public string YoutubeTrailer { get; set; }
    }

    public class VodStreamInfo
    {
        public int StreamId { get; set; }
        
        public string ContainerExtension { get; set; }
    }
}
