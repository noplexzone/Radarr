using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Movies.Editions
{
    public static class EditionNormalizer
    {
        private static readonly Regex SeparatorRegex = new Regex(@"[._\-]+", RegexOptions.Compiled);
        private static readonly Regex PossessiveRegex = new Regex(@"\b(director|collector)'?s\b", RegexOptions.Compiled);
        private static readonly Regex AmpersandRegex = new Regex(@"\s*&\s*", RegexOptions.Compiled);
        private static readonly Regex InvalidCharsRegex = new Regex(@"[^\p{L}\p{N}' ]+", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex SuffixRegex = new Regex(@"(?<=\S)\s+(cut|edition|version)$", RegexOptions.Compiled);
        private static readonly Regex ArticleRegex = new Regex(@"^the\s+(?=\S)", RegexOptions.Compiled);

        // Produces the key used to decide whether two edition names describe the same edition,
        // e.g. "Director's Cut", "Directors.Cut" and "Director's Edition" all become "director's".
        // A leading "The" is dropped because release names often omit it ("The Final Cut" vs "Final.Cut").
        public static string Normalize(string edition)
        {
            if (edition.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var key = edition.ToLowerInvariant().Replace('’', '\'').Replace('`', '\'');

            key = SeparatorRegex.Replace(key, " ");
            key = AmpersandRegex.Replace(key, " and ");
            key = InvalidCharsRegex.Replace(key, " ");
            key = PossessiveRegex.Replace(key, "$1's");
            key = WhitespaceRegex.Replace(key, " ").Trim();
            key = ArticleRegex.Replace(key, string.Empty);
            key = SuffixRegex.Replace(key, string.Empty);

            return key;
        }
    }
}
