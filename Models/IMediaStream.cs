using System;

namespace ModernIPTVPlayer.Models
{
    public interface IMediaStream
    {
        int Id { get; }
        string Title { get; }
        string PosterUrl { get; }
        string Rating { get; }
        // We can add more common properties as needed for the Details Page
    }
}
