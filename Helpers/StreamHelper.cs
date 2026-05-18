using System;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;
using ModernIPTVPlayer.Models.Iptv;
using ModernIPTVPlayer.Models.Stremio;
using ModernIPTVPlayer.Services.Stremio;

namespace ModernIPTVPlayer.Helpers
{
    /// <summary>
    /// Static domain helpers for stream classifications, comparisons, and matching logic.
    /// Extracted from MediaInfoPage to maintain clean view-logic boundaries.
    /// </summary>
    public static class StreamHelper
    {
        public static bool IsSameItem(IMediaStream item1, IMediaStream item2)
        {
            if (item1 == null || item2 == null) return false;
            
            // 1. Exact ID match (IMDb, TMDB, or internal)
            string id1 = item1.IMDbId ?? item1.Id.ToString();
            string id2 = item2.Id.ToString(); // Use raw ID as secondary fallback
            if (!string.IsNullOrEmpty(item1.IMDbId)) id1 = item1.IMDbId;
            
            string rawId1 = item1.IMDbId;
            string rawId2 = item2.IMDbId;

            bool hasStableId1 = !string.IsNullOrEmpty(rawId1) && (rawId1.StartsWith("tt") || rawId1.StartsWith("tmdb:"));
            bool hasStableId2 = !string.IsNullOrEmpty(rawId2) && (rawId2.StartsWith("tt") || rawId2.StartsWith("tmdb:"));

            // [CRITICAL] If both have stable IDs and they differ -> Definitely different items.
            if (hasStableId1 && hasStableId2 && rawId1 != rawId2) 
            {
                return false;
            }

            // 2. ID Equality check (including non-stable IDs or cases where one is missing)
            if (!string.IsNullOrEmpty(rawId1) && !string.IsNullOrEmpty(rawId2) && rawId1 == rawId2) return true;

            // 3. Robust Fallback: Title + Year + Type
            // This handles cases where one item (usually IPTV) lacks a stable ID.
            if (!string.IsNullOrEmpty(item1.Title) && !string.IsNullOrEmpty(item2.Title))
            {
                bool titleMatch = item1.Title.Trim().Equals(item2.Title.Trim(), StringComparison.OrdinalIgnoreCase);
                
                string y1 = StremioService.GetYearDigits(item1.Year);
                string y2 = StremioService.GetYearDigits(item2.Year);
                bool yearMatch = (string.IsNullOrEmpty(y1) || string.IsNullOrEmpty(y2)) || (y1 == y2);
                
                // Content Type check (Movie vs Series)
                string t1 = item1.Type?.ToLowerInvariant() ?? "";
                string t2 = item2.Type?.ToLowerInvariant() ?? "";
                bool typeMatch = (string.IsNullOrEmpty(t1) || string.IsNullOrEmpty(t2)) || (t1 == t2 || (t1 == "tv" && t2 == "series") || (t1 == "series" && t2 == "tv"));

                if (titleMatch && yearMatch && typeMatch)
                {
                    return true;
                }
            }
            
            return false;
        }

        public static bool IsSeriesItem(IMediaStream item, UnifiedMetadata unifiedMetadata = null)
        {
            if (item == null) return false;
            if (item is SeriesStream) return true;
            if (item is StremioMediaStream sms && (sms.Meta.Type == "series" || sms.Meta.Type == "tv")) return true;
            if (!string.IsNullOrEmpty(item.Type) && (item.Type.Equals("SERIES", StringComparison.OrdinalIgnoreCase) || item.Type.Equals("TV", StringComparison.OrdinalIgnoreCase))) return true;
            if (unifiedMetadata != null && unifiedMetadata.IsSeries) return true;
            return false;
        }
    }
}
