namespace ModernIPTVPlayer
{
    public class LoginParams
    {
        public string? Host { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? PlaylistUrl { get; set; }
        public int MaxConnections { get; set; } = 1;
    }
}