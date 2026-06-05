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
            if (searchCriteria is not MovieSearchCriteria movieCriteria ||
                movieCriteria.MovieEditionSlotId is null ||
                string.IsNullOrWhiteSpace(movieCriteria.EditionSearchTerm))
            {
                return DownloadSpecDecision.Accept();
            }

            var wanted = EditionNormalizer.Normalize(movieCriteria.EditionSearchTerm);

            var parsedEdition = subject.ParsedMovieInfo?.Edition;
            if (!string.IsNullOrWhiteSpace(parsedEdition) && EditionNormalizer.Normalize(parsedEdition) == wanted)
            {
                _logger.Debug("Release edition '{0}' matches requested edition '{1}'", parsedEdition, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            var releaseTitle = subject.Release?.Title;
            if (!string.IsNullOrWhiteSpace(releaseTitle) && EditionNormalizer.Normalize(releaseTitle).Contains(wanted))
            {
                _logger.Debug("Release title '{0}' contains requested edition '{1}'", releaseTitle, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            _logger.Debug("Release edition does not match requested edition: wanted '{0}'", movieCriteria.EditionSearchTerm);
            return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
        }
    }
}
