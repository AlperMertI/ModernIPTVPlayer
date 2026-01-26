using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer
{
    public class VodInfoResponse
    {
        [JsonPropertyName("info")]
        public VodInfo Info { get; set; }
        
        [JsonPropertyName("movie_data")]
        public VodStreamInfo MovieData { get; set; }
    }

    public class VodInfo
    {
        [JsonPropertyName("name")] // Title
        public string Name { get; set; }
        
        [JsonPropertyName("description")] // Plot
        public string Description { get; set; }
        
        [JsonPropertyName("director")]
        public string Director { get; set; }
        
        [JsonPropertyName("cast")]
        public string Cast { get; set; }
        
        [JsonPropertyName("rating")]
        public string Rating { get; set; }
        
        [JsonPropertyName("releasedate")]
        public string ReleaseDate { get; set; }
        
        [JsonPropertyName("backdrop_path")]
        public string[] BackdropPath { get; set; }
        
        [JsonPropertyName("genre")]
        public string Genre { get; set; }

        [JsonPropertyName("youtube_trailer")]
        public string YoutubeTrailer { get; set; }
    }

    public class VodStreamInfo
    {
        [JsonPropertyName("stream_id")]
        public int StreamId { get; set; }
        
        [JsonPropertyName("container_extension")]
        public string ContainerExtension { get; set; }
    }
}
