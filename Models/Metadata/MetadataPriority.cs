using System;

namespace ModernIPTVPlayer.Models.Metadata
{
    public static class MetadataPriority
    {
        // Authority Levels
        public const int AUTHORITY_TMDB = 10;
        public const int AUTHORITY_HISTORY = 8; // [NEW] Protect metadata from items already in user's history
        public const int AUTHORITY_STREMIO_ADDON = 5;
        public const int AUTHORITY_IPTV = 2;
        public const int AUTHORITY_NONE = 0;

        // Enrichment Depths
        public const int DEPTH_DETAIL = 3;    // Full probe (Episodes, Cast, etc.)
        public const int DEPTH_SPOTLIGHT = 2; // Mid-level (Trailers, Backdrops)
        public const int DEPTH_CATALOG = 0;   // Basic list entry (Title, Poster)

        /// <summary>
        /// Calculates a comparative priority score.
        /// Higher score = Higher priority.
        /// </summary>
        /// <param name="authority">The source authority (TMDB > Addon > IPTV)</param>
        /// <param name="depth">The enrichment depth (Detail > Catalog)</param>
        /// <param name="addonRank">The index of the addon in the user's list (0 = top priority)</param>
        /// <returns>A score used for priority comparison</returns>
        public static int CalculateScore(int authority, int depth, int addonRank = 0)
        {
            // Rank is subtracted to ensure lower index (higher user preference) results in a higher score.
            // We cap rank at 99 to ensure it doesn't bleed into the depth/authority ranges.
            int safetyRank = Math.Min(Math.Max(addonRank, 0), 99);
            return (authority * 1000) + (depth * 100) - (safetyRank * 10);
        }
    }
}
