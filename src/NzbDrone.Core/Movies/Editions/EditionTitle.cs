using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Movies.Editions
{
    public static class EditionTitle
    {
        private static readonly HashSet<string> Acronyms = new HashSet<string> { "3D", "4K", "DC", "HD", "HDR", "IMAX", "SDR", "TV", "UHD", "UK", "US" };
        private static readonly HashSet<string> SmallWords = new HashSet<string> { "a", "and", "of", "the" };

        private static readonly Regex SeparatorRegex = new Regex(@"[._\s]+", RegexOptions.Compiled);
        private static readonly Regex ExtCutRegex = new Regex(@"\bextcut\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PossessiveRegex = new Regex(@"\b(Director|Collector)s\b", RegexOptions.Compiled);
        private static readonly Regex NumberWordRegex = new Regex(@"^\d+(st|nd|rd|th|in\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Turns an edition name taken from a release or file name into a display name,
        // e.g. "UNRATED" becomes "Unrated" and "Directors Cut" becomes "Director's Cut".
        // Words already in mixed case, as in a Plex tag the user wrote, are kept.
        public static string Format(string edition)
        {
            if (edition.IsNullOrWhiteSpace())
            {
                return edition;
            }

            var title = SeparatorRegex.Replace(edition, " ").Trim();
            title = ExtCutRegex.Replace(title, "Extended Cut");

            var words = title.Split(' ').Select((word, index) => FormatWord(word, index == 0));

            return PossessiveRegex.Replace(string.Join(" ", words), "$1's");
        }

        private static string FormatWord(string word, bool isFirst)
        {
            var upper = word.ToUpperInvariant();
            var lower = word.ToLowerInvariant();

            if (Acronyms.Contains(upper))
            {
                return upper;
            }

            if (NumberWordRegex.IsMatch(word))
            {
                return lower;
            }

            if (word != upper && word != lower)
            {
                return word;
            }

            if (!isFirst && SmallWords.Contains(lower))
            {
                return lower;
            }

            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }
    }
}
