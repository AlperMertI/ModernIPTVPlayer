using System;
using System.Collections.Generic;
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

                // Smart Merging (Backfill mode: Append new unique items)
                target.Genres = MergeLists(target.Genres, source.Genres, true);
                target.Cast = MergeLists(target.Cast, source.Cast != null ? string.Join(", ", source.Cast.Select(c => c.Name)) : null, true);
                target.Director = MergeLists(target.Director, source.Directors != null ? string.Join(", ", source.Directors.Select(d => d.Name)) : null, true);
                
                if (string.IsNullOrEmpty(target.TrailerUrl) && !string.IsNullOrEmpty(source.TrailerUrl)) 
                    target.TrailerUrl = source.TrailerUrl;
            }
            else
            {
                // [ENRICHMENT] Update with better/equal quality data
                if (!string.IsNullOrEmpty(source.Title) && target.Title != source.Title) 
                {
                    // [TITLE PROTECTION] For CW series items, enrichment provides Series Title, 
                    // but we must preserve the Episode Title already in target.Title.
                    if (target is Stremio.StremioMediaStream sStream && sStream.IsContinueWatching && sStream.IsSeries)
                    {
                        sStream.SeriesName = source.Title;
                    }
                    else
                    {
                        target.Title = source.Title;
                    }
                }
                
                if (!string.IsNullOrEmpty(source.Overview) && target.Description != source.Overview) 
                    target.Description = source.Overview;
                
                if (!string.IsNullOrEmpty(source.Year) && target.Year != source.Year) 
                    target.Year = source.Year;
                
                if (source.Rating > 0)
                {
                    string rStr = source.Rating.ToString("F1");
                    if (target.Rating != rStr) target.Rating = rStr;
                }

                // Smart Merging (Enrichment mode: Prepend priority items)
                target.Genres = MergeLists(target.Genres, source.Genres, false);
                target.Cast = MergeLists(target.Cast, source.Cast != null ? string.Join(", ", source.Cast.Select(c => c.Name)) : null, false);
                target.Director = MergeLists(target.Director, source.Directors != null ? string.Join(", ", source.Directors.Select(d => d.Name)) : null, false);
                
                if (!string.IsNullOrEmpty(source.TrailerUrl)) 
                {
                    target.TrailerUrl = source.TrailerUrl;
                }
                if (!string.IsNullOrEmpty(source.SourceTitle)) target.SourceTitle = source.SourceTitle;
            }

            // 2. High-Priority Fields (Always sync if present in source)
            if (source.TmdbInfo != null) target.TmdbInfo = source.TmdbInfo;
            if (!string.IsNullOrEmpty(source.PosterUrl)) target.PosterUrl = source.PosterUrl;

            // [BACKDROP INTELLIGENCE] 
            // If primary BackdropUrl is missing, try picking the first one from the list
            string? bestBackdrop = source.BackdropUrl;
            if (string.IsNullOrEmpty(bestBackdrop) && source.BackdropUrls != null && source.BackdropUrls.Count > 0)
            {
                bestBackdrop = source.BackdropUrls.FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(bestBackdrop)) target.BackdropUrl = bestBackdrop;

            // 3. Special Case: Stremio-specific fields
            if (target is Stremio.StremioMediaStream stremioFinal)
            {
                // Strict backfill for Logo/Id
                if (!string.IsNullOrEmpty(source.LogoUrl))
                {
                    if (!backfillOnly || string.IsNullOrEmpty(stremioFinal.LogoUrl)) stremioFinal.LogoUrl = source.LogoUrl;
                }

                if (!string.IsNullOrEmpty(source.ImdbId))
                {
                    if (!backfillOnly || string.IsNullOrEmpty(stremioFinal.Meta.Id)) stremioFinal.Meta.Id = source.ImdbId;
                }
                
                if (!string.IsNullOrEmpty(source.Genres))
                {
                     if (!backfillOnly || string.IsNullOrEmpty(stremioFinal.Genres))
                     {
                          stremioFinal.Genres = source.Genres;
                     }
                }
            }
        }

        private static string? MergeLists(string? current, string? incoming, bool backfillOnly)
        {
            if (string.IsNullOrEmpty(incoming)) return current;
            if (string.IsNullOrEmpty(current)) return incoming;

            var currentList = current.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            var incomingList = incoming.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

            var result = new List<string>(currentList);
            foreach (var item in incomingList)
            {
                if (!result.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase)))
                {
                    if (backfillOnly)
                        result.Add(item); // Append for lower priority
                    else
                        result.Insert(0, item); // Prepend for higher priority
                }
            }

            return string.Join(", ", result);
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
