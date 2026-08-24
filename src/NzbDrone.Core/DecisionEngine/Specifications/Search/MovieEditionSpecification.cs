using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
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
            if (searchCriteria is not MovieSearchCriteria movieCriteria)
            {
                return DownloadSpecDecision.Accept();
            }

            if (subject.EditionMatchResult != null)
            {
                return EvaluateStructuredMatch(subject.EditionMatchResult, movieCriteria.AcquisitionTarget);
            }

            if (movieCriteria.MovieEditionSlotId is null)
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

        private DownloadSpecDecision EvaluateStructuredMatch(EditionMatchResult match, MovieAcquisitionTarget target)
        {
            if (target.Kind == MovieAcquisitionTargetKind.Main)
            {
                if (match.Status == EditionMatchStatus.NoEditionEvidence)
                {
                    return DownloadSpecDecision.Accept();
                }

                if (match.Status == EditionMatchStatus.UniqueSlot)
                {
                    return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                        $"Release matches configured edition '{match.SelectedSlot.EditionName}' (slot {match.SelectedSlot.Id}), not Main");
                }

                return RejectUnsafeStructuredMatch(match);
            }

            if (target.Kind != MovieAcquisitionTargetKind.EditionSlot || !target.EditionSlotId.HasValue)
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    "Release acquisition target is unknown and cannot be matched safely");
            }

            if (match.Status != EditionMatchStatus.UniqueSlot)
            {
                return RejectUnsafeStructuredMatch(match);
            }

            if (match.SelectedSlot.Id != target.EditionSlotId.Value)
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    $"Edition target mismatch: release matches slot {match.SelectedSlot.Id}, but slot {target.EditionSlotId.Value} was requested");
            }

            return DownloadSpecDecision.Accept();
        }

        private DownloadSpecDecision RejectUnsafeStructuredMatch(EditionMatchResult match)
        {
            var message = match.Status switch
            {
                EditionMatchStatus.Ambiguous => $"Release edition evidence matches multiple configured edition slots ({string.Join(", ", match.CandidateSlotIds)}): {match.Reason}",
                EditionMatchStatus.UnknownEdition => match.Reason,
                EditionMatchStatus.Invalid => $"Release edition could not be mapped safely: {match.Reason}",
                EditionMatchStatus.NoEditionEvidence => "Release edition could not be mapped safely to the requested edition slot",
                _ => match.Reason
            };

            _logger.Debug("Edition target rejected: target-independent status={0} source={1} candidateSlotIds=[{2}] reason={3}",
                match.Status,
                match.Source,
                string.Join(",", match.CandidateSlotIds),
                message);
            return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition, message);
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
