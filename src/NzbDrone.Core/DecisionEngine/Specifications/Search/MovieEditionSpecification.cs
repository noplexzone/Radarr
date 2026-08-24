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
        private static readonly Regex ApostropheRegex = new Regex("['’‘]", RegexOptions.Compiled);
        private static readonly Regex AbbreviationRegex = new Regex(@"(?<![\p{L}\p{Nd}])(?:[\p{L}\p{Nd}][\p{P}\p{S}_])+[\p{L}\p{Nd}](?=$|[^\p{L}\p{Nd}])", RegexOptions.Compiled);
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);
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
                        $"Release matches configured edition '{match.SelectedSlotEditionName}' (slot {match.SelectedSlotId.Value}), not Main");
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

            if (match.SelectedSlotId.Value != target.EditionSlotId.Value)
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    $"Edition target mismatch: release matches slot {match.SelectedSlotId.Value}, but slot {target.EditionSlotId.Value} was requested");
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
            var titleTokens = TokenizeEditionText(subject.Release?.Title)
                .Select(token => new TitleToken(token))
                .ToList();
            foreach (var movieTitle in subject.ParsedMovieInfo?.MovieTitles ?? new List<string>())
            {
                MaskTokenSequence(titleTokens, TokenizeEditionText(movieTitle));
            }

            MaskTokenSequence(titleTokens, TokenizeEditionText(subject.ParsedMovieInfo?.ReleaseGroup));
            return ContainsEditionTokens(titleTokens, TokenizeEditionText(editionTerm));
        }

        public static bool TitleContainsEditionTerm(string releaseTitle, string editionTerm)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || string.IsNullOrWhiteSpace(editionTerm))
            {
                return false;
            }

            var titleTokens = TokenizeEditionText(releaseTitle).Select(token => new TitleToken(token)).ToList();
            return ContainsEditionTokens(titleTokens, TokenizeEditionText(editionTerm));
        }

        private static bool ContainsEditionTokens(List<TitleToken> titleTokens, List<string> termTokens)
        {
            if (!termTokens.Any() || termTokens.Count > titleTokens.Count)
            {
                return false;
            }

            for (var start = 0; start <= titleTokens.Count - termTokens.Count; start++)
            {
                var candidate = titleTokens.Skip(start).Take(termTokens.Count).ToList();
                if (candidate.Any(token => token.IsMasked) || !candidate.Select(token => token.Value).SequenceEqual(termTokens))
                {
                    continue;
                }

                var nextIndex = start + termTokens.Count;
                if (nextIndex < titleTokens.Count && !titleTokens[nextIndex].IsMasked && new[] { "enhanced", "edition", "cut" }.Contains(titleTokens[nextIndex].Value))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static void MaskTokenSequence(List<TitleToken> source, List<string> sequence)
        {
            if (!sequence.Any() || sequence.Count > source.Count)
            {
                return;
            }

            for (var start = source.Count - sequence.Count; start >= 0; start--)
            {
                var candidate = source.Skip(start).Take(sequence.Count).ToList();
                if (candidate.Select(token => token.Value).SequenceEqual(sequence))
                {
                    candidate.ForEach(token => token.IsMasked = true);
                }
            }
        }

        private static List<string> TokenizeEditionText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            var apostropheNormalized = ApostropheRegex.Replace(value, string.Empty);
            var abbreviationNormalized = AbbreviationRegex.Replace(apostropheNormalized,
                match => EditionNormalizer.Normalize(match.Value));
            return TokenRegex.Matches(abbreviationNormalized)
                .Select(match => EditionNormalizer.Normalize(match.Value))
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
        }

        private sealed class TitleToken
        {
            public TitleToken(string value)
            {
                Value = value;
            }

            public string Value { get; }
            public bool IsMasked { get; set; }
        }
    }
}
