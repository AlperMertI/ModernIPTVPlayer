using System;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models
{
    public class XtreamAuthResponse
    {
        [JsonPropertyName("user_info")]
        public XtreamUserInfo UserInfo { get; set; }

        [JsonPropertyName("server_info")]
        public XtreamServerInfo ServerInfo { get; set; }
    }

    public class XtreamUserInfo
    {
        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("max_connections")]
        public string MaxConnectionsStr { get; set; }

        [JsonPropertyName("active_cons")]
        public string ActiveConnectionsStr { get; set; }

        [JsonIgnore]
        public int MaxConnections => int.TryParse(MaxConnectionsStr, out int val) ? val : 1;

        [JsonIgnore]
        public int ActiveConnections => int.TryParse(ActiveConnectionsStr, out int val) ? val : 0;
    }

    public class XtreamServerInfo
    {
        [JsonPropertyName("timezone")]
        public string Timezone { get; set; }

        [JsonPropertyName("server_protocol")]
        public string Protocol { get; set; }
    }
}
