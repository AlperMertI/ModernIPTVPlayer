using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ModernIPTVPlayer.Services;

namespace ModernIPTVPlayer.Helpers
{
    public static class TitleHelper
    {
        // NO GLOBAL UNBOUNDED CACHE (Project Zero Phase 2)
        // Static caches removed to prevent heap bloat with large playlists.
        
        public static void ClearCaches()
        {
            // No-op now as we don't hold static dictionaries
        }

        private static readonly string[] Articles = { 
            "the", "a", "an", "der", "die", "das", "le", "la", "les", "el", "la", "los", "las", 
            "of", "and", "in", "or", "to", "for", "with", "from", "at", "by", "on", "as", "is", "it", "its" 
        };
        private static readonly Regex YearRegex = new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);

        /// <summary>
        /// Robustly compares two titles using token-based fuzzy matching.
        /// Handles leading articles, IPTV tags, technical suffixes, and year mismatches.
        /// </summary>
        public static bool IsMatch(IEnumerable<string?> candidates, string? target, string? candidateYear = null, string? targetYear = null)
        {
            if (string.IsNullOrWhiteSpace(target) || candidates == null) return false;
            return candidates.Any(c => IsMatch(c, target, candidateYear, targetYear));
        }

        /// <summary>
        /// Robustly compares two titles using token-based fuzzy matching.
        /// Handles leading articles, IPTV tags, technical suffixes, and year mismatches.
        /// </summary>
        public static bool IsMatch(string? title1, string? title2, string? year1 = null, string? year2 = null)
        {
            if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2)) return false;

            var tokens1 = GetSignificantTokens(title1);
            var tokens2 = GetSignificantTokens(title2);
            
            return IsMatch(tokens1, tokens2, title1, title2, year1, year2);
        }

        public static bool IsMatch(HashSet<string> tokens1, HashSet<string> tokens2, string? rawTitle1 = null, string? rawTitle2 = null, string? year1 = null, string? year2 = null)
        {
            if (tokens1.Count == 0 || tokens2.Count == 0) return false;

            // 1. Year Reinforcement
            string y1 = string.IsNullOrWhiteSpace(year1) ? ExtractYear(rawTitle1) : ExtractYear(year1);
            string y2 = string.IsNullOrWhiteSpace(year2) ? ExtractYear(rawTitle2) : ExtractYear(year2);
            bool hasYearMatch = false;

            if (!string.IsNullOrEmpty(y1) && !string.IsNullOrEmpty(y2))
            {
                if (y1 != y2) 
                {
                    AppLogger.Warn($"[TitleMatch] Year REJECT: '{rawTitle1}' ({y1}) vs '{rawTitle2}' ({y2})");
                    return false;
                }
                hasYearMatch = true;
            }
            bool oneSideHasYear = !string.IsNullOrEmpty(y1) || !string.IsNullOrEmpty(y2);

            // [NEW] Ambiguity Protection: For very short/generic titles, require exact year match if years are available.
            // This prevents "Spider-Man" (no year) from matching "Spider-Man" (2017) if it's potentially a different version.
            if (!hasYearMatch && oneSideHasYear && (GetBaseTokens(rawTitle1 ?? "").Count <= 1 || GetBaseTokens(rawTitle2 ?? "").Count <= 1))
            {
                AppLogger.Warn($"[TitleMatch] Ambiguity REJECT: '{rawTitle1}' vs '{rawTitle2}'. Generic/Short title requires exact year match.");
                return false;
            }

            // 2. Strict Digit/Sequel Check - Compare numbers and Roman Numerals (e.g. Spider-Man 2 vs 3, Watchmen I vs II)
            var numeric1 = tokens1.Where(t => (t.Any(char.IsDigit) && (!int.TryParse(t, out int n) || n < 50)) || IsRomanNumeral(t)).ToList();
            var numeric2 = tokens2.Where(t => (t.Any(char.IsDigit) && (!int.TryParse(t, out int n) || n < 50)) || IsRomanNumeral(t)).ToList();
            if (numeric1.Count != numeric2.Count || !numeric1.All(d => numeric2.Contains(d))) 
            {
                AppLogger.Warn($"[TitleMatch] Numeric/Sequel REJECT: '{rawTitle1}' vs '{rawTitle2}'. Numerics: [{string.Join(", ", numeric1)}] vs [{string.Join(", ", numeric2)}]");
                return false;
            }
            // 3. Similarity Check
            double similarity = CalculateSimilarityInternal(tokens1, tokens2);
            
            // [RELAXED] If year matches exactly, we are very lenient (40%) to handle long titles matching localized versions.
            // Example: "Your Friendly Neighborhood Spider-Man" (5 tokens) vs "Spider-Man" (2 tokens) -> 0.40
            double threshold = hasYearMatch ? 0.40 : (oneSideHasYear ? 0.95 : 0.85);

            if (similarity >= threshold)
            {
                // [TIGHTENED] Unique Word Penalty: e.g. "Spider-Man" vs "Spider-Man: Lotus"
                // If one title has a significant word (length > 3) NOT in the other, and it's not a year match.
                var unique1 = tokens1.Except(tokens2).Where(t => t.Length > 3).ToList();
                var unique2 = tokens2.Except(tokens1).Where(t => t.Length > 3).ToList();
                
                if (!hasYearMatch && (unique1.Any() || unique2.Any()))
                {
                    // [REFINEMENT] If we don't have a year match, and one title has extra significant words, it's a mismatch
                    // unless the similarity is absolute (100%) or we have a high-confidence subset match (0.98+)
                    if (similarity < 0.98)
                    {
                        AppLogger.Warn($"[TitleMatch] Unique Word REJECT: '{rawTitle1}' vs '{rawTitle2}'. Similarity {similarity:F2} >= {threshold:F2}, but unique words (no year): [{string.Join(", ", unique1)}] | [{string.Join(", ", unique2)}]");
                        return false;
                    }
                }
                return true;
            }

            AppLogger.Warn($"[TitleMatch] Similarity REJECT: '{rawTitle1}' vs '{rawTitle2}'. Similarity {similarity:F2} < threshold {threshold:F2} (YearMatch: {hasYearMatch})");
            return false;
        }

        private static double CalculateSimilarityInternal(HashSet<string> tokens1, HashSet<string> tokens2)
        {
            if (tokens1.Count == 0 || tokens2.Count == 0) return 0;

            // [FIX] Ignore composites for similarity and subset calculation to avoid noise
            var real1 = tokens1.Where(t => !IsComposite(t)).ToHashSet(); // Title
            var real2 = tokens2.Where(t => !IsComposite(t)).ToHashSet(); // Query

            if (real1.Count == 0 || real2.Count == 0) return 0;

            int common = real1.Intersect(real2).Count();
            int max = Math.Max(real1.Count, real2.Count);
            
            // 1. Full Query Match (Query is a total subset of Title)
            // Example: Query "no way home" (3) is matched inside "Spider-Man: No Way Home" (5)
            // This is VERY STRONG. Give it nearly perfect score (0.97-1.0)
            if (common == real2.Count)
            {
                // Penalize slightly based on how much "extra" stuff is in the title 
                // but keep it much higher than partial matches.
                return 0.98 - (0.02 * (real1.Count - real2.Count) / real1.Count);
            }

            // 2. Partial Query Match (Title is a subset of Query)
            // Example: Query "no way home" (3) matches title "Way Home" (2)
            // This is WEAKER because it's missing words from the user's intent.
            if (common == real1.Count)
            {
                return 0.85 + (0.05 * common / real2.Count);
            }

            return (double)common / max;
        }

        /// <summary>
        /// Universally normalizes a string: strips tags, language prefixes, diacritics, and non-alphanumeric.
        /// </summary>
        public static string Normalize(string? title)
        {
            if (string.IsNullOrEmpty(title)) return string.Empty;

            // 1. Strip Common IPTV Prefixes at the start of string (e.g. "TR - ", "4K | ", "IT:")
            string cleaned = Regex.Replace(title, @"^.{1,4}\s*[:\-\|]\s*", " ", RegexOptions.IgnoreCase);

            // 2. Strip brackets/parentheses and their content
            cleaned = Regex.Replace(cleaned, @"\[.*?\]|\(.*?\)", " ");
            
            // 3. Strip common IPTV Language Codes anywhere as words
            string langPattern = @"\b(TR|ENG|TUR|GER|FRA|IT|ES|DE|FR|PL|RO|RU|AR|PT|BR|HE|NL|HI|ZH|JA|KO|SV|FI|DA|CS|HU|SK|EL|VI|TH|ID|MS|FA|UK|KA|AZ|BE|ET|LV|LT|MK|SQ|SR|HR|BS|SL|IS|AF|ZU|XH|ST|TN|SS|NR|NF|IR|GR|EN|US|UK|CA|AU)\b";
            cleaned = Regex.Replace(cleaned, langPattern, " ", RegexOptions.IgnoreCase);

            // [NEW] Strip Adult Content Tags
            string adultPattern = @"\b(XXX|ADULT|PORN|PURN|MATURE|NSFW|HENTAI|JAV|PVR|EROTIC|SEX|SEKS)\b";
            cleaned = Regex.Replace(cleaned, adultPattern, " ", RegexOptions.IgnoreCase);

            // 4. Strip Technical Tags
            string techPattern = @"\b(4K|2K|FHD|HD|SD|1080p|720p|480p|BluRay|BRRip|DVDRip|WebRip|Web-DL|x264|x265|h264|h265|HEVC|HDR|Dual|Multi|UHD|10BIT|8BIT|REPACK|EXTENDED|DIRECTORS)\b";
            cleaned = Regex.Replace(cleaned, techPattern, " ", RegexOptions.IgnoreCase);

            // 5. Unicode normalization (FormD) and filter
            string normalized = cleaned.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(c);
                    else if (char.IsWhiteSpace(c)) sb.Append(' ');
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormC).Replace("İ", "i").Replace("I", "i").ToLowerInvariant()
                     .Replace(" ", ""); 
        }

        public static HashSet<string> GetSignificantTokens(string title)
        {
            if (string.IsNullOrEmpty(title)) return new HashSet<string>();
            return GetSignificantTokensInternal(title, true);
        }

        public static HashSet<string> GetBaseTokens(string title)
        {
            if (string.IsNullOrEmpty(title)) return new HashSet<string>();
            return GetSignificantTokensInternal(title, false);
        }

        private static HashSet<string> GetSignificantTokensInternal(string title, bool includeComposite)
        {
            // 1. Strip technical and language tags completely before tokenizing
            string cleaned = Regex.Replace(title, @"\[.*?\]|\(.*?\)", " ");
            
            // Remove common IPTV country/source prefixes at the start (e.g., "IL - ", "TR | ")
            cleaned = Regex.Replace(cleaned, @"^.{1,4}\s*[:\-\|]\s*", " ", RegexOptions.IgnoreCase);

            // [FIX] Removed "NO" from language list because it's a common word in English titles
            string langPattern = @"\b(IL|TR|ENG|TUR|GER|FRA|IT|ES|DE|FR|PL|RO|RU|AR|PT|BR|HE|NL|HI|ZH|JA|KO|SV|FI|DA|CS|HU|SK|EL|VI|TH|ID|MS|FA|UK|KA|AZ|BE|ET|LV|LT|MK|SQ|SR|HR|BS|SL|IS|AF|ZU|XH|ST|TN|SS|NR)\b\s*[:\-\|]?\s*";
            cleaned = Regex.Replace(cleaned, langPattern, " ", RegexOptions.IgnoreCase);

            string techPattern = @"\b(4K|2K|FHD|HD|SD|1080p|720p|480p|BluRay|BRRip|DVDRip|WebRip|Web-DL|x264|x265|h264|h265|HEVC|HDR|Dual|Multi|UHD|10BIT|8BIT|XXX|ADULT|PORN)\b";
            cleaned = Regex.Replace(cleaned, techPattern, " ", RegexOptions.IgnoreCase);

            // 2. Unicode decomposition to handle accents
            cleaned = cleaned.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in cleaned)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(c);
                    else sb.Append(' ');
                }
            }

            var words = sb.ToString().Replace("İ", "i").Replace("I", "i").ToLowerInvariant()
                          .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var tokens = words.Where(w => !Articles.Contains(w) && !YearRegex.IsMatch(w) && (w.Length > 1 || (w.Length == 1 && (char.IsDigit(w[0]) || IsRomanNumeral(w)))))
                        .ToHashSet();

            if (includeComposite && tokens.Count > 1 && tokens.Count <= 3)
            {
                string composite = string.Join("", tokens.OrderBy(t => t));
                if (composite.Length > 3) tokens.Add("comp_" + composite); // Mark as composite
            }

            return tokens;
        }

        private static bool IsComposite(string token) => token.StartsWith("comp_");

        private static bool IsRomanNumeral(string? t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            string val = t.ToLowerInvariant();
            // Common Roman Numerals used in titles (1-10)
            return val == "i" || val == "ii" || val == "iii" || val == "iv" || val == "v" || 
                   val == "vi" || val == "vii" || val == "viii" || val == "ix" || val == "x";
        }

        public static double CalculateSimilarity(string title1, string title2)
        {
            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2)) return 0;
            return CalculateSimilarityInternal(GetSignificantTokens(title1), GetSignificantTokens(title2));
        }


        public static string ExtractYear(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            // 1. Check for YYYY-MM-DD or YYYY format at start
            if (input.Length >= 4 && char.IsDigit(input[0]) && char.IsDigit(input[1]) && char.IsDigit(input[2]) && char.IsDigit(input[3]))
            {
                string first4 = input.Substring(0, 4);
                if (int.TryParse(first4, out int year) && year > 1900 && year < 2100)
                    return first4;
            }

            // 2. Bracketed year: (2024), [2024]
            var bracketMatch = Regex.Match(input, @"[\(\[]\s*((?:19|20)\d{2})\s*[\)\]]");
            if (bracketMatch.Success) return bracketMatch.Groups[1].Value;

            // 3. Year at the end or after a separator: "Title - 2024" or "Title: 2024"
            var suffixMatch = Regex.Match(input, @"[:\-]\s*((?:19|20)\d{2})\b");
            if (suffixMatch.Success) return suffixMatch.Groups[1].Value;

            // 4. Standalone 4-digit year: "Title 2024 Subtitle"
            var standaloneMatch = Regex.Match(input, @"\b((?:19|20)\d{2})\b");
            if (standaloneMatch.Success) return standaloneMatch.Groups[1].Value;

            return "";
        }
    }
}
