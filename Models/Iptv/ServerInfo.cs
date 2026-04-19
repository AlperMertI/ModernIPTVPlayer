using System;
using System.Text.Json.Serialization;

namespace ModernIPTVPlayer.Models
{
    public class XtreamAuthResponse
    {
        public XtreamUserInfo UserInfo { get; set; }

        public XtreamServerInfo ServerInfo { get; set; }
    }

    public class XtreamUserInfo
    {
        public string Username { get; set; }

        public string Status { get; set; }

        public string MaxConnections { get; set; }

        public string ActiveCons { get; set; }

        public System.Text.Json.JsonElement ExpDate { get; set; }

        [JsonIgnore]
        public string ExpiryDateUnix => ExpDate.ValueKind == System.Text.Json.JsonValueKind.Null ? null : ExpDate.ToString();

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
        public int MaxConnectionsCount => int.TryParse(MaxConnections, out int val) ? val : 1;

        [JsonIgnore]
        public int ActiveConnectionsCount => int.TryParse(ActiveCons, out int val) ? val : 0;
    }

    public class XtreamServerInfo
    {
        public string Timezone { get; set; }

        public string ServerProtocol { get; set; }
    }
}
