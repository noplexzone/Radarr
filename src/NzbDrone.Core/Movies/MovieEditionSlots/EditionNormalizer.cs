using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public static class EditionNormalizer
    {
        // Strip separators/punctuation so "Director's Cut", `Directors.Cut`, and
        // `"Directors Cut"` all collapse to the same token for comparison.
        // Double-quotes are included so user-supplied values with surrounding
        // quotes don't create false mismatches at the decision engine.
        private static readonly Regex NormalizeRegex = new Regex(@"[\s\-_.'""]+", RegexOptions.Compiled);

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
