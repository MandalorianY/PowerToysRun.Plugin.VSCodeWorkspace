// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;

namespace Community.PowerToys.Run.Plugin.VSCodeWorkspaces
{
    /// <summary>
    /// Provides fuzzy search functionality for matching strings
    /// </summary>
    public static class FuzzySearchHelper
    {
        /// <summary>
        /// Calculates a fuzzy match score between a query and target string
        /// </summary>
        /// <param name="query">The search query</param>
        /// <param name="target">The target string to match against</param>
        /// <returns>Score from 0-100, where 100 is a perfect match</returns>
        public static int CalculateScore(string query, string target)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target))
                return 0;

            query = query.ToLowerInvariant();
            target = target.ToLowerInvariant();

            // Exact match gets highest score
            if (target.Equals(query))
                return 100;

            // Starts with query gets high score
            if (target.StartsWith(query))
                return 90;

            // Contains query as substring gets good score
            if (target.Contains(query))
                return 80;

            // Calculate character-by-character fuzzy match
            return CalculateFuzzyScore(query, target);
        }

        /// <summary>
        /// Calculates fuzzy score based on character matching with position weighting
        /// </summary>
        private static int CalculateFuzzyScore(string query, string target)
        {
            if (query.Length == 0 || target.Length == 0)
                return 0;

            int matches = 0;
            int consecutiveMatches = 0;
            int maxConsecutive = 0;
            int queryIndex = 0;
            bool previousMatch = false;

            for (int targetIndex = 0; targetIndex < target.Length && queryIndex < query.Length; targetIndex++)
            {
                if (target[targetIndex] == query[queryIndex])
                {
                    matches++;
                    queryIndex++;

                    if (previousMatch)
                    {
                        consecutiveMatches++;
                    }
                    else
                    {
                        consecutiveMatches = 1;
                        previousMatch = true;
                    }

                    maxConsecutive = Math.Max(maxConsecutive, consecutiveMatches);
                }
                else
                {
                    previousMatch = false;
                    consecutiveMatches = 0;
                }
            }

            // If we didn't match all query characters, it's not a valid match
            if (queryIndex < query.Length)
                return 0;

            // Calculate score based on:
            // - Percentage of query characters matched
            // - Bonus for consecutive matches
            // - Penalty for target length (shorter targets score higher)
            double matchRatio = (double)matches / query.Length;
            double consecutiveBonus = (double)maxConsecutive / query.Length * 0.3;
            double lengthPenalty = Math.Min(0.2, (double)(target.Length - query.Length) / target.Length);

            int score = (int)((matchRatio + consecutiveBonus - lengthPenalty) * 70);
            return Math.Max(0, Math.Min(70, score)); // Cap at 70 for fuzzy matches
        }

        /// <summary>
        /// Checks if a target string has a fuzzy match with any of the search tokens
        /// </summary>
        /// <param name="searchTokens">Array of search tokens</param>
        /// <param name="targets">Target strings to search in</param>
        /// <param name="minimumScore">Minimum score required for a match (default: 30)</param>
        /// <returns>Maximum score across all targets and tokens</returns>
        public static int GetBestMatchScore(string[] searchTokens, string[] targets, int minimumScore = 30)
        {
            if (searchTokens.Length == 0)
                return 100; // No search terms means everything matches

            int bestScore = 0;

            foreach (var token in searchTokens)
            {
                int tokenBestScore = 0;
                
                foreach (var target in targets)
                {
                    if (!string.IsNullOrEmpty(target))
                    {
                        int score = CalculateScore(token, target);
                        tokenBestScore = Math.Max(tokenBestScore, score);
                    }
                }

                // All tokens must have at least minimum score
                if (tokenBestScore < minimumScore)
                    return 0;

                bestScore += tokenBestScore;
            }

            // Average the scores across all tokens
            return bestScore / searchTokens.Length;
        }
    }
}