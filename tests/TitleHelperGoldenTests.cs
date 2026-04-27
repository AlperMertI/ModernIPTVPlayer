using System;
using System.Collections.Generic;
using System.Linq;
using ModernIPTVPlayer.Helpers;

namespace ModernIPTVPlayer.Tests
{
    /// <summary>
    /// Smoke tests to verify 100% output parity for TitleHelper refactoring.
    /// Bit-to-bit comparison of normalization and tokenization results.
    /// </summary>
    public static class TitleHelperGoldenTests
    {
        private static readonly List<(string Input, string Normalized, string[] Tokens)> TestCases = new()
        {
            (
                "TR: Avatar 2 The Way of Water (2022) [1080p] | EN",
                "avatar 2 the way of water 2022 1080p en",
                new[] { "avatar", "2", "way", "water" }
            ),
            (
                "[VOD] Inception (2010) UHD.BluRay.x264-ADULT",
                "vod inception 2010 uhd bluray x264 adult",
                new[] { "inception" }
            ),
            (
                "beIN Sports 1 HD (TR-ENG) | FHD",
                "bein sports 1 hd tr eng fhd",
                new[] { "bein", "sports", "1" }
            ),
            (
                "L'Étranger (The Stranger) - 1967",
                "letranger the stranger 1967",
                new[] { "letranger", "stranger" }
            ),
            (
                "Avengers: Endgame (2019) 4K MULTI-SUBS",
                "avengers endgame 2019 4k multi subs",
                new[] { "avengers", "endgame" }
            ),
            (
                "Adult: Sexy Movie (2023) XXX",
                "adult sexy movie 2023 xxx",
                new[] { "sexy", "movie" }
            )
        };

        public static void Run()
        {
            int passed = 0;
            int total = TestCases.Count;

            Console.WriteLine("[TitleHelperGoldenTests] Starting parity audit...");

            foreach (var test in TestCases)
            {
                // 1. Check Normalize
                string norm = TitleHelper.Normalize(test.Input);
                bool normMatch = (norm.Trim() == test.Normalized.Trim());

                // 2. Check Tokens (Pinnacle Iterator)
                int tokenCount = 0;
                bool allTokensMatch = true;
                foreach (var token in TitleHelper.GetTokens(test.Input.AsSpan()))
                {
                    if (tokenCount >= test.Tokens.Length || !token.Equals(test.Tokens[tokenCount].AsSpan(), StringComparison.OrdinalIgnoreCase))
                    {
                        allTokensMatch = false;
                        break;
                    }
                    tokenCount++;
                }
                bool tokensMatch = allTokensMatch && tokenCount == test.Tokens.Length;

                if (normMatch && tokensMatch)
                {
                    passed++;
                    Console.WriteLine($"[PASS] {test.Input}");
                }
                else
                {
                    Console.WriteLine($"[FAIL] {test.Input}");
                    if (!normMatch) Console.WriteLine($"  Expected Norm: '{test.Normalized}' | Got: '{norm}'");
                    if (!tokensMatch) Console.WriteLine($"  Expected Tokens: [{string.Join(", ", test.Tokens)}] | Count mismatch or content mismatch.");
                }
            }

            Console.WriteLine($"[TitleHelperGoldenTests] Results: {passed}/{total} PASSED");
            
            if (passed < total)
            {
                throw new Exception("TitleHelper parity check FAILED. Refactoring aborted or needs fix.");
            }
        }
    }
}
