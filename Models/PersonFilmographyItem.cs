using Microsoft.UI.Xaml.Media;
using System;
using ModernIPTVPlayer.Models.Stremio;

namespace ModernIPTVPlayer.Models
{
    [Microsoft.UI.Xaml.Data.Bindable]
    public class PersonFilmographyItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Character { get; set; }
        public string PosterPath { get; set; }
        public double VoteAverage { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string MediaType { get; set; }
        public bool IsCast { get; set; }

        public ImageSource Poster
        {
            get
            {
                if (string.IsNullOrEmpty(PosterPath)) return null;
                if (PosterPath.StartsWith("http")) 
                    return ModernIPTVPlayer.ImageHelper.GetImage(PosterPath);
                return ModernIPTVPlayer.ImageHelper.GetImage($"https://image.tmdb.org/t/p/w185{PosterPath}");
            }
        }

        public PersonFilmographyItem() { }

        public PersonFilmographyItem(StremioMediaStream m)
        {
            Id = m.Id;
            Title = m.Title;
            Character = "";
            PosterPath = m.PosterUrl;
            VoteAverage = 0;
            ReleaseDate = null;
            MediaType = m.Type;
            IsCast = true;
        }
    }
}
