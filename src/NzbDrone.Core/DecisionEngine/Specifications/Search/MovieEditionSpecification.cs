using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.Search
{
    public class MovieEditionSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public MovieEditionSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, SearchCriteriaBase searchCriteria)
        {
            if (searchCriteria is not MovieSearchCriteria movieCriteria || movieCriteria.MovieEditionSlotId is null)
            {
                return DownloadSpecDecision.Accept();
            }

            if (string.IsNullOrWhiteSpace(movieCriteria.EditionSearchTerm))
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    "Edition slot search is missing an edition search term");
            }

            var matchTerms = movieCriteria.EditionMatchTerms?.Where(term => !string.IsNullOrWhiteSpace(term)).ToList();
            if (matchTerms?.Any() != true)
            {
                matchTerms = new List<string> { movieCriteria.EditionSearchTerm };
            }

            var parsedEdition = subject.ParsedMovieInfo?.Edition;
            if (!string.IsNullOrWhiteSpace(parsedEdition))
            {
                if (matchTerms.Any(term => EditionNormalizer.Normalize(parsedEdition) == EditionNormalizer.Normalize(term)))
                {
                    _logger.Debug("Release edition '{0}' matches requested edition slot", parsedEdition);
                    return DownloadSpecDecision.Accept();
                }

                _logger.Debug("Parsed release edition '{0}' does not match requested edition slot", parsedEdition);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
            }

            var releaseTitle = subject.Release?.Title;
            if (matchTerms.Any(term => TitleContainsEditionTerm(subject, term)))
            {
                _logger.Debug("Release title '{0}' contains a configured identity for requested edition '{1}'", releaseTitle, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            _logger.Debug("Release edition does not match requested edition: wanted '{0}'", movieCriteria.EditionSearchTerm);
            return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
        }

        public static bool TitleContainsEditionTerm(RemoteMovie subject, string editionTerm)
        {
            var titleTokens = TokenizeEditionText(subject.Release?.Title);
            foreach (var movieTitle in subject.ParsedMovieInfo?.MovieTitles ?? new List<string>())
            {
                RemoveTokenSequence(titleTokens, TokenizeEditionText(movieTitle));
            }

            RemoveTokenSequence(titleTokens, TokenizeEditionText(subject.ParsedMovieInfo?.ReleaseGroup));
            return ContainsEditionTokens(titleTokens, TokenizeEditionText(editionTerm));
        }

        public static bool TitleContainsEditionTerm(string releaseTitle, string editionTerm)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || string.IsNullOrWhiteSpace(editionTerm))
            {
                return false;
            }

            return ContainsEditionTokens(TokenizeEditionText(releaseTitle), TokenizeEditionText(editionTerm));
        }

        private static bool ContainsEditionTokens(List<string> titleTokens, List<string> termTokens)
        {
            if (!termTokens.Any() || termTokens.Count > titleTokens.Count)
            {
                return false;
            }

            for (var start = 0; start <= titleTokens.Count - termTokens.Count; start++)
            {
                if (!titleTokens.Skip(start).Take(termTokens.Count).SequenceEqual(termTokens))
                {
                    continue;
                }

                var nextIndex = start + termTokens.Count;
                if (nextIndex < titleTokens.Count && new[] { "enhanced", "edition", "cut" }.Contains(titleTokens[nextIndex]))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static void RemoveTokenSequence(List<string> source, List<string> sequence)
        {
            if (!sequence.Any() || sequence.Count > source.Count)
            {
                return;
            }

            for (var start = source.Count - sequence.Count; start >= 0; start--)
            {
                if (source.Skip(start).Take(sequence.Count).SequenceEqual(sequence))
                {
                    source.RemoveRange(start, sequence.Count);
                }
            }
        }

        private static List<string> TokenizeEditionText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            var apostropheNormalized = value.Replace("'", string.Empty);
            return Regex.Matches(apostropheNormalized, @"[A-Za-z0-9]+")
                .Select(match => EditionNormalizer.Normalize(match.Value))
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
        }
    }
}
