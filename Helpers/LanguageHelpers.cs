using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ModernIPTVPlayer.Helpers
{
    public static class LanguageHelpers
    {
        private static readonly Dictionary<string, string> _customMappings = new()
        {
            { "tur", "Türkçe" },
            { "tr", "Türkçe" },
            { "eng", "English" },
            { "en", "English" },
            { "deu", "Deutsch" },
            { "de", "Deutsch" },
            { "ger", "Deutsch" },
            { "fra", "Français" },
            { "fr", "Français" },
            { "fre", "Français" },
            { "ita", "Italiano" },
            { "it", "Italiano" },
            { "es", "Español" },
            { "spa", "Español" },
            { "rus", "Russian" },
            { "ru", "Russian" },
            { "ara", "Arabic" },
            { "ar", "Arabic" },
            { "por", "Portuguese" },
            { "pt", "Portuguese" },
            { "jpn", "Japanese" },
            { "ja", "Japanese" },
            { "kor", "Korean" },
            { "ko", "Korean" },
            { "chi", "Chinese" },
            { "zh", "Chinese" },
            { "zho", "Chinese" },
            { "und", "Bilinmiyor" },
            { "unk", "Bilinmiyor" }
        };

        public static string GetDisplayName(string isoCode)
        {
            if (string.IsNullOrWhiteSpace(isoCode)) return "Bilinmiyor";

            string lowerCode = isoCode.ToLowerInvariant().Trim();

            // 1. Check custom override map first
            if (_customMappings.TryGetValue(lowerCode, out string mappedName))
            {
                return mappedName;
            }

            // 2. Try CultureInfo
            try
            {
                var culture = new CultureInfo(lowerCode);
                return culture.NativeName; // e.g. "Türkçe", "English"
            }
            catch
            {
                // Fallback: If 3 letter code fails, try to find via Neutral Cultures
                try
                {
                   var cultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures);
                   var found = cultures.FirstOrDefault(c => c.ThreeLetterISOLanguageName.Equals(lowerCode, StringComparison.OrdinalIgnoreCase));
                   if (found != null) return found.NativeName;
                }
                catch { }
            }

            // 3. Last Resort: Return the code itself (uppercase)
            return lowerCode.ToUpperInvariant();
        }
    }
}
