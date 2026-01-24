using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace ModernIPTVPlayer
{
    public class SeriesStream
    {
        [JsonPropertyName("num")]
        public object Num { get; set; } // Sometimes int, sometimes string

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("series_id")]
        public int SeriesId { get; set; }

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("plot")]
        public string? Plot { get; set; }

        [JsonPropertyName("cast")]
        public string? Cast { get; set; }

        [JsonPropertyName("director")]
        public string? Director { get; set; }

        [JsonPropertyName("genre")]
        public string? Genre { get; set; }

        [JsonPropertyName("releaseDate")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("last_modified")]
        public string? LastModified { get; set; }

        [JsonPropertyName("rating")]
        public string? Rating { get; set; } // Can be "5", "5.0", etc.

        [JsonPropertyName("rating_5based")]
        public object Rating5Based { get; set; }

        [JsonPropertyName("backdrop_path")]
        public object[]? BackdropPath { get; set; }

        [JsonPropertyName("youtube_trailer")]
        public string? YoutubeTrailer { get; set; }

        [JsonPropertyName("episode_run_time")]
        public string? EpisodeRunTime { get; set; }

        [JsonPropertyName("category_id")]
        public string? CategoryId { get; set; }

        // Helper for UI binding
        public BitmapImage? CoverImage
        {
            get
            {
                if (string.IsNullOrEmpty(Cover)) return null;
                try
                {
                    return new BitmapImage(new Uri(Cover));
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
