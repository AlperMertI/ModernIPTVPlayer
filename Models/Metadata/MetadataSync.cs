using System;
using System.Linq;
using ModernIPTVPlayer.Models;
using ModernIPTVPlayer.Models.Metadata;

namespace ModernIPTVPlayer.Models.Metadata
{
    /// <summary>
    /// Centralized logic for synchronizing metadata from UnifiedMetadata back to any IMediaStream implementation.
    /// This eliminates duplicated 'if' logic across multiple stream classes.
    /// </summary>
    public static class MetadataSync
    {
        public static void Sync(IMediaStream target, UnifiedMetadata source, bool backfillOnly = false)
        {
            if (target == null || source == null) return;

            // 1. Conditional Enrichment (Backfill or Overwrite)
            if (backfillOnly)
            {
                // [SAFEGUARD] Only fill if currently missing or placeholder
                if (IsTitleMissing(target.Title) && !string.IsNullOrEmpty(source.Title)) 
                    target.Title = source.Title;
                
                if (string.IsNullOrEmpty(target.Description) && !string.IsNullOrEmpty(source.Overview)) 
                    target.Description = source.Overview;
                
                if (string.IsNullOrEmpty(target.Year) && !string.IsNullOrEmpty(source.Year)) 
                    target.Year = source.Year;
            }
            else
            {
                // [ENRICHMENT] Update with better/equal quality data
                if (!string.IsNullOrEmpty(source.Title) && target.Title != source.Title) 
                    target.Title = source.Title;
                
                if (!string.IsNullOrEmpty(source.Overview) && target.Description != source.Overview) 
                    target.Description = source.Overview;
                
                if (!string.IsNullOrEmpty(source.Year) && target.Year != source.Year) 
                    target.Year = source.Year;
                
                if (source.Rating > 0)
                {
                    string rStr = source.Rating.ToString("F1");
                    if (target.Rating != rStr) target.Rating = rStr;
                }
            }

            // 2. High-Priority Fields (Always sync if present in source)
            // Note: Setters in implementing classes should handle OnPropertyChanged
            if (source.TmdbInfo != null) target.TmdbInfo = source.TmdbInfo;
            if (!string.IsNullOrEmpty(source.PosterUrl)) target.PosterUrl = source.PosterUrl;
            if (!string.IsNullOrEmpty(source.BackdropUrl)) target.BackdropUrl = source.BackdropUrl;

            // 3. Special Case: Stremio-specific fields
            if (target is Stremio.StremioMediaStream stremio)
            {
                if (!string.IsNullOrEmpty(source.LogoUrl)) stremio.LogoUrl = source.LogoUrl;
                if (!string.IsNullOrEmpty(source.ImdbId)) stremio.Meta.Id = source.ImdbId;
                
                if (!string.IsNullOrEmpty(source.Genres))
                {
                     if (!backfillOnly || string.IsNullOrEmpty(stremio.Genres))
                     {
                          stremio.Meta.Genres = source.Genres.Split(", ").ToList();
                          stremio.OnPropertyChanged("Genres");
                     }
                }

                // Update internal safeguard level
                if (!backfillOnly)
                {
                    stremio.CurrentEnrichmentLevel = source.MaxEnrichmentContext;
                }
            }
        }

        private static bool IsTitleMissing(string title)
        {
            return string.IsNullOrEmpty(title) || 
                   title == "Loading..." || 
                   title == "Film" || 
                   title == "Kanal" || 
                   title == "Dizi";
        }
    }
}
