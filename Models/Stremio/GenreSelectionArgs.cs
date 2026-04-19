namespace ModernIPTVPlayer.Models.Stremio
{
    public class GenreSelectionArgs
    {
        public string AddonId { get; set; }
        public string CatalogId { get; set; }
        public string CatalogType { get; set; } // "movie" or "series"
        public string GenreValue { get; set; } // e.g. "Action", "Animasyon", "2024"
        public string FilterKey { get; set; } // e.g. "genre", "year"
        public string DisplayName { get; set; } // For title display
    }
}
