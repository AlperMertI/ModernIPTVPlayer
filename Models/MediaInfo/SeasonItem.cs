using System.Collections.Generic;

namespace ModernIPTVPlayer
{
    /// <summary>
    /// UI-facing season row used by MediaInfoPage season selection.
    /// Kept in the root namespace so existing XAML bindings continue to resolve.
    /// </summary>
    [Microsoft.UI.Xaml.Data.Bindable]
    public class SeasonItem
    {
        public string Name { get; set; }
        public string SeasonName { get; set; }
        public int SeasonNumber { get; set; }
        public List<EpisodeItem> Episodes { get; set; }
        public bool IsEnrichedByTmdb { get; set; }
    }
}
