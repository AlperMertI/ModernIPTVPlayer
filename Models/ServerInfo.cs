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

        [JsonPropertyName("exp_date")]
        public System.Text.Json.JsonElement ExpiryDateElement { get; set; }

        [JsonIgnore]
        public string ExpiryDateUnix => ExpiryDateElement.ValueKind == System.Text.Json.JsonValueKind.Null ? null : ExpiryDateElement.ToString();

        [JsonIgnore]
        public string FormattedExpiryDate
        {
            get
            {
                string raw = ExpiryDateUnix;
                if (string.IsNullOrEmpty(raw) || raw == "0" || raw == "\"0\"") return "Sonsuz";
                
                // Trimming quotes if it came as a string element
                raw = raw.Trim('"');

                if (long.TryParse(raw, out long unixTime))
                {
                    try
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
                        return dt.ToString("dd.MM.yyyy");
                    }
                    catch { }
                }
                return "Belirsiz";
            }
        }

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
