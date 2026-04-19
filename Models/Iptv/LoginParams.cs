namespace ModernIPTVPlayer.Models.Iptv
{
    public class LoginParams
    {
        public string? Host { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? PlaylistUrl { get; set; }
        public string? PlaylistId { get; set; }
        public string? PlaylistName { get; set; }
        public int MaxConnectionsCount { get; set; } = 1;
    }
}
