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

            var wanted = EditionNormalizer.Normalize(movieCriteria.EditionSearchTerm);

            var parsedEdition = subject.ParsedMovieInfo?.Edition;
            if (!string.IsNullOrWhiteSpace(parsedEdition))
            {
                if (EditionNormalizer.Normalize(parsedEdition) == wanted)
                {
                    _logger.Debug("Release edition '{0}' matches requested edition '{1}'", parsedEdition, movieCriteria.EditionSearchTerm);
                    return DownloadSpecDecision.Accept();
                }

                _logger.Debug("Parsed release edition '{0}' does not match requested edition '{1}'", parsedEdition, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                    $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
            }

            var releaseTitle = subject.Release?.Title;
            if (TitleContainsEditionTerm(releaseTitle, movieCriteria.EditionSearchTerm))
            {
                _logger.Debug("Release title '{0}' contains requested edition '{1}'", releaseTitle, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            _logger.Debug("Release edition does not match requested edition: wanted '{0}'", movieCriteria.EditionSearchTerm);
            return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
        }

        public static bool TitleContainsEditionTerm(string releaseTitle, string editionTerm)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle) || string.IsNullOrWhiteSpace(editionTerm))
            {
                return false;
            }

            var normalizedTitle = EditionNormalizer.Normalize(releaseTitle);
            var normalizedTerm = EditionNormalizer.Normalize(editionTerm);

            if (!normalizedTitle.Contains(normalizedTerm))
            {
                return false;
            }

            // Avoid treating a shorter slot name as a match for a more specific adjacent edition tag.
            return !normalizedTitle.Contains(normalizedTerm + "enhanced") &&
                   !normalizedTitle.Contains(normalizedTerm + "edition") &&
                   !normalizedTitle.Contains(normalizedTerm + "cut");
        }
    }
}
