namespace ModernIPTVPlayer.Models.Metadata
{
    /// <summary>
    /// Defines the context in which metadata is requested.
    /// This determines the "Required Fields" and enrichment depth.
    /// </summary>
    public enum MetadataContext 
    { 
        /// <summary> Default catalog row item (Standard Vertical Poster) </summary>
        Discovery = 0, 
        
        /// <summary> Horizontal catalog row item (Lite enrichment) </summary>
        Landscape = 1,

        /// <summary> Top carousel item (VIP enrichment + Trailer/Logo/Overview) </summary>
        Spotlight = 2, 

        /// <summary> Top banner item (Full enrichment + Genres/Overview) </summary>
        Hero = 3,

        /// <summary> Quick details view </summary>
        ExpandedCard = 4, 

        /// <summary> Full details page </summary>
        Detail = 5 
    }
}
