using System.Collections.Generic;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    public class MovieSearchCriteria : SearchCriteriaBase
    {
        // Retained so callers can inspect the raw edition term (e.g. for logging or future
        // decision-engine filtering).
        // TODO(multi-edition): decision-engine specification that rejects grabbed releases
        // that do not match the requested edition remains unimplemented.
        public string EditionSearchTerm { get; set; }

        // Returns a merged list of edition-appended variants followed by the base titles.
        // Edition variants come first so indexers encounter them before plain-title queries.
        // Base titles are always kept for interactive/manual fallback.
        // Returns a copy of baseTitles unchanged when editionSearchTerm is null or whitespace.
        public static List<string> BuildEditionTitles(List<string> baseTitles, string editionSearchTerm)
        {
            if (string.IsNullOrWhiteSpace(editionSearchTerm))
            {
                return new List<string>(baseTitles);
            }

            var edition = editionSearchTerm.Trim();
            var result = new List<string>(baseTitles.Count * 2);
            foreach (var title in baseTitles)
            {
                result.Add($"{title} {edition}");
            }

            result.AddRange(baseTitles);
            return result;
        }

        public override string ToString()
        {
            return string.Format("[{0}]", Movie.Title);
        }
    }
}
