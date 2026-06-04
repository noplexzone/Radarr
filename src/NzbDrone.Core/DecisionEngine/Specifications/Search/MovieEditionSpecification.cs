using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.Search
{
    public class MovieEditionSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly Logger _logger;

        // Strip punctuation/spaces so "Director's Cut" and "Directors.Cut" both normalise to "directorscut"
        private static readonly Regex NormaliseRegex = new Regex(@"[\s\-_.'""]+", RegexOptions.Compiled);

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

            var wanted = Normalise(movieCriteria.EditionSearchTerm);

            var parsedEdition = subject.ParsedMovieInfo?.Edition;
            if (!string.IsNullOrWhiteSpace(parsedEdition) && Normalise(parsedEdition).Contains(wanted))
            {
                _logger.Debug("Release edition '{0}' matches requested edition '{1}'", parsedEdition, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            var releaseTitle = subject.Release?.Title;
            if (!string.IsNullOrWhiteSpace(releaseTitle) && Normalise(releaseTitle).Contains(wanted))
            {
                _logger.Debug("Release title '{0}' contains requested edition '{1}'", releaseTitle, movieCriteria.EditionSearchTerm);
                return DownloadSpecDecision.Accept();
            }

            _logger.Debug("Release edition does not match requested edition: wanted '{0}'", movieCriteria.EditionSearchTerm);
            return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition,
                $"Release edition does not match requested edition: wanted {movieCriteria.EditionSearchTerm}");
        }

        private static string Normalise(string input) =>
            NormaliseRegex.Replace(input, string.Empty).ToLowerInvariant();
    }
}
