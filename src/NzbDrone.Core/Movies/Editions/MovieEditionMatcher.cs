using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Movies.Editions
{
    public enum MovieEditionMatchType
    {
        Matched,
        Default,
        New,
        Ambiguous
    }

    public class MovieEditionMatch
    {
        public MovieEditionMatchType Type { get; private set; }

        // The existing edition, or a transient one (Id 0) for a new edition
        public MovieEdition Edition { get; private set; }
        public string Reason { get; private set; }

        public bool IsResolved => Type != MovieEditionMatchType.Ambiguous;

        public static MovieEditionMatch Matched(MovieEdition edition) => new MovieEditionMatch { Type = MovieEditionMatchType.Matched, Edition = edition };
        public static MovieEditionMatch Default(MovieEdition edition) => new MovieEditionMatch { Type = MovieEditionMatchType.Default, Edition = edition };
        public static MovieEditionMatch New(MovieEdition edition) => new MovieEditionMatch { Type = MovieEditionMatchType.New, Edition = edition };
        public static MovieEditionMatch Ambiguous(string reason) => new MovieEditionMatch { Type = MovieEditionMatchType.Ambiguous, Reason = reason };

        public override string ToString()
        {
            return Type == MovieEditionMatchType.Ambiguous ? $"{Type}: {Reason}" : $"{Type}: {Edition}";
        }
    }

    public static class MovieEditionMatcher
    {
        private static readonly Regex MultiCutRegex = new Regex(@"\b[2-4] ?in ?1\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // e.g. "Final Cut Directors Cut", a release naming two cuts
        private static readonly Regex TwoCutsRegex = new Regex(@"\bcut\b.*\bcut\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Words describing a restoration rather than a different cut of the movie
        private static readonly Regex QualifierRegex = new Regex(@"\b(4k |uhd )?(remastered|remaster|restored|restoration)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static MovieEditionMatch Match(int movieId, IEnumerable<MovieEdition> movieEditions, string parsedEdition, bool monitorNewEdition = true)
        {
            var editions = movieEditions.ToList();
            var defaultEdition = editions.Single(e => e.IsDefault);

            if (parsedEdition.IsNullOrWhiteSpace())
            {
                return MovieEditionMatch.Default(defaultEdition);
            }

            if (MultiCutRegex.IsMatch(parsedEdition) || TwoCutsRegex.IsMatch(parsedEdition))
            {
                return MovieEditionMatch.Ambiguous($"'{parsedEdition}' contains more than one cut of the movie");
            }

            var key = EditionNormalizer.Normalize(parsedEdition);
            var exact = editions.FirstOrDefault(e => e.Key == key);

            if (exact != null)
            {
                return exact.IsDefault ? MovieEditionMatch.Default(exact) : MovieEditionMatch.Matched(exact);
            }

            var withoutQualifiers = EditionNormalizer.Normalize(QualifierRegex.Replace(key, " "));

            if (withoutQualifiers.IsNotNullOrWhiteSpace() && withoutQualifiers != key)
            {
                // "Final Cut Remastered" is the Final Cut edition, but only a release without an edition name is the Standard edition
                var qualified = editions.FirstOrDefault(e => e.Key == withoutQualifiers && !e.IsDefault);

                if (qualified != null)
                {
                    return MovieEditionMatch.Matched(qualified);
                }
            }

            var contained = editions.Where(e => e.Key.IsNotNullOrWhiteSpace() && ContainsPhrase(key, e.Key)).ToList();

            if (contained.Count > 1)
            {
                return MovieEditionMatch.Ambiguous($"'{parsedEdition}' matches several editions: {string.Join(", ", contained.Select(e => e.DisplayTitle))}");
            }

            return MovieEditionMatch.New(MovieEdition.CreateNew(movieId, EditionTitle.Format(parsedEdition), monitorNewEdition));
        }

        private static bool ContainsPhrase(string key, string phrase)
        {
            var words = key.Split(' ');
            var phraseWords = phrase.Split(' ');

            for (var i = 0; i <= words.Length - phraseWords.Length; i++)
            {
                if (words.Skip(i).Take(phraseWords.Length).SequenceEqual(phraseWords))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
