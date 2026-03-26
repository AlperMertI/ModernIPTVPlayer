using System;

namespace ModernIPTVPlayer.Services
{
    public static class PageStateProvider
    {
        public static string LastMovieCategoryId { get; set; }
        public static string LastSeriesCategoryId { get; set; }
        public static Models.MediaType LastMediaType { get; set; } = Models.MediaType.Movie;
    }
}
