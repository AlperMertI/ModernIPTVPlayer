using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models.Tmdb
{
    public class TmdbSearchResponse
    {
        public List<TmdbMovieResult> Results { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbMovieResult
    {
        public int Id { get; set; }
        
        public string Title { get; set; }

        public string OriginalTitle { get; set; }
        
        public string Name { get; set; } 

        public string OriginalName { get; set; }

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrEmpty(Title) ? Title : Name;

        [JsonIgnore]
        public string DisplayOriginalTitle => !string.IsNullOrEmpty(OriginalTitle) ? OriginalTitle : OriginalName;
        
        public string Overview { get; set; }
        
        public string BackdropPath { get; set; }
        
        public string PosterPath { get; set; }
        
        public double VoteAverage { get; set; }

        public string ReleaseDate { get; set; }

        public string FirstAirDate { get; set; }

        [JsonIgnore]
        public string DisplayDate => !string.IsNullOrEmpty(ReleaseDate) ? ReleaseDate : FirstAirDate;

        public List<int> GenreIds { get; set; }

        public TmdbImages Images { get; set; }

        public List<TmdbSeason> Seasons { get; set; }

        public string? ImdbId { get; set; }

        public TmdbExternalIds? ExternalIds { get; set; }

        public string? ResolvedImdbId => ImdbId ?? ExternalIds?.ImdbId;

        public string GetGenreNames()
        {
            if (GenreIds == null || GenreIds.Count == 0) return "Genel";
            
            var names = new List<string>();
            foreach (var id in GenreIds.Take(3))
            {
                if (_genreMap.TryGetValue(id, out string name))
                    names.Add(name);
            }
            
            return names.Count > 0 ? string.Join(" • ", names) : "Genel";
        }

        private static readonly Dictionary<int, string> _genreMap = new Dictionary<int, string>
        {
            {28, "Aksiyon"}, {12, "Macera"}, {16, "Animasyon"}, {35, "Komedi"}, {80, "Suç"},
            {99, "Belgesel"}, {18, "Dram"}, {10751, "Aile"}, {14, "Fantastik"}, {36, "Tarih"},
            {27, "Korku"}, {10402, "Müzik"}, {9648, "Gizem"}, {10749, "Romantik"}, {878, "Bilim Kurgu"},
            {10770, "TV Film"}, {53, "Gerilim"}, {10752, "Savaş"}, {37, "Vahşi Batı"},
            {10759, "Aksiyon & Macera"}, {10762, "Çocuk"}, {10763, "Haber"}, {10764, "Reality"},
            {10765, "Bilim Kurgu & Fantazi"}, {10766, "Pembe Dizi"}, {10767, "Talk Show"}, {10768, "Savaş & Politik"}
        };

        public string FullBackdropUrl => !string.IsNullOrEmpty(BackdropPath) ? $"https://image.tmdb.org/t/p/w1280{BackdropPath}" : null;
    }

    public class TmdbVideosResponse
    {
        public List<TmdbVideo> Results { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbVideo
    {
        public string Key { get; set; } 
        
        public string Site { get; set; }
        
        public string Type { get; set; }

        public string Iso639_1 { get; set; }

        public string Iso3166_1 { get; set; }

        public string Name { get; set; }
    }

    public class TmdbCreditsResponse
    {
        public List<TmdbCast> Cast { get; set; }

        public List<TmdbCrew> Crew { get; set; }
    }

    public class TmdbImages
    {
        public List<TmdbImage> Backdrops { get; set; }

        public List<TmdbImage> Logos { get; set; }

        public List<TmdbImage> Posters { get; set; }
    }

    public class TmdbImage
    {
        public string FilePath { get; set; }

        public string Iso639_1 { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbCast
    {
        public string Name { get; set; }
        
        public string Character { get; set; }
        
        public string ProfilePath { get; set; }

        public string FullProfileUrl => !string.IsNullOrEmpty(ProfilePath) ? $"https://image.tmdb.org/t/p/w185{ProfilePath}" : null;
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbCrew
    {
        public string Name { get; set; }

        public string Job { get; set; }

        public string Department { get; set; }

        public string ProfilePath { get; set; }

        public string FullProfileUrl => !string.IsNullOrEmpty(ProfilePath) ? $"https://image.tmdb.org/t/p/w185{ProfilePath}" : null;
    }

    public class TmdbMovieDetails
    {
        public int? Runtime { get; set; } 

        public string Overview { get; set; }

        public List<TmdbGenre> Genres { get; set; }

        public string ImdbId { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbGenre
    {
        public string Name { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbSeason
    {
        public int Id { get; set; }

        public int SeasonNumber { get; set; }

        public string Name { get; set; }

        public int EpisodeCount { get; set; }
    }

    public class TmdbSeasonDetails
    {
        public string PosterPath { get; set; }

        public List<TmdbEpisode> Episodes { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbEpisode
    {
        public int EpisodeNumber { get; set; }

        public string Name { get; set; }

        public string Overview { get; set; }
        
        public string StillPath { get; set; }

        public int? Runtime { get; set; }

        public string AirDate { get; set; }

        public DateTime? AirDateDateTime => !string.IsNullOrEmpty(AirDate) && DateTime.TryParse(AirDate, out var d) ? d : null;
        
        public string StillUrl => !string.IsNullOrEmpty(StillPath) ? $"https://image.tmdb.org/t/p/w300{StillPath}" : null;
    }

    public class TmdbExternalIds
    {
        public string? ImdbId { get; set; }

        public System.Text.Json.JsonElement? TvdbId { get; set; }
    }

    public class TmdbPersonSearchResult
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string ProfilePath { get; set; }

        public string KnownForDepartment { get; set; }

        public List<TmdbPersonKnownFor> KnownFor { get; set; }
    }

    public class TmdbPersonKnownFor
    {
        public string Title { get; set; }

        public string Name { get; set; }

        public string PosterPath { get; set; }

        public string MediaType { get; set; }
    }

    public class TmdbPersonDetails
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string Biography { get; set; }

        public DateTime? Birthday { get; set; }

        public string PlaceOfBirth { get; set; }

        public string ProfilePath { get; set; }

        public string KnownForDepartment { get; set; }
    }

    [Microsoft.UI.Xaml.Data.Bindable]
    public class TmdbPersonCredit
    {
        public int Id { get; set; }

        public string Title { get; set; }

        public string Name { get; set; }

        public string PosterPath { get; set; }

        public string Character { get; set; }

        public string MediaType { get; set; }

        public string ReleaseDate { get; set; }

        public string FirstAirDate { get; set; }

        public double VoteAverage { get; set; }

        public double Popularity { get; set; }

        public List<int> GenreIds { get; set; }

        public string Job { get; set; }

        public string Department { get; set; }
    }

    public class TmdbPersonCreditsResponse
    {
        public List<TmdbPersonCredit> Cast { get; set; }

        public List<TmdbPersonCredit> Crew { get; set; }
    }

    public class TmdbPersonSearchResponse
    {
        public List<TmdbPersonSearchResult> Results { get; set; }
    }
}
