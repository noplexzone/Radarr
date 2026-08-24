using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public static class EditionNormalizer
    {
        // Strip Unicode punctuation, separators, and symbols so apostrophe and release-style
        // punctuation variants collapse to the same deterministic identity.
        private static readonly Regex NormalizeRegex = new Regex(@"[\p{P}\p{Z}\p{S}\s_]+", RegexOptions.Compiled);

        public static string Normalize(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return NormalizeRegex.Replace(value, string.Empty).ToLowerInvariant();
        }
    }
}
