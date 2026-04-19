using System;
using System.Collections.Generic;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbPersonInfo
    {
        public int TmdbId { get; set; }
        public string Name { get; set; }
        public string Biography { get; set; }
        public DateTime? Birthday { get; set; }
        public string? BirthPlace { get; set; }
        public string? ProfilePath { get; set; }
        public List<string>? Awards { get; set; }
        public List<string>? BackdropUrls { get; set; }
        public List<PersonRoleItem>? RecentRoles { get; set; }
        public bool HasTmdbData => TmdbId > 0;
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class PersonRoleItem
    {
        public string Title { get; set; }
        public string Role { get; set; }
        public string PosterPath { get; set; }
        public string MediaType { get; set; }
        public Microsoft.UI.Xaml.Media.ImageSource Poster => 
            !string.IsNullOrEmpty(PosterPath) ? ImageHelper.GetImage(TmdbHelper.GetImageUrl(PosterPath, "w185")) : null;
        public IMediaStream Stream { get; set; }
    }
}
