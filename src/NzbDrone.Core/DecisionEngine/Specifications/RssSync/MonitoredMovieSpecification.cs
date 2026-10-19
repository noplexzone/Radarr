using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications.RssSync
{
    public class MonitoredMovieSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public MonitoredMovieSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, ReleaseDecisionInformation information)
        {
            if (information.SearchCriteria is { UserInvokedSearch: true })
            {
                _logger.Debug("Skipping monitored check during search");
                return DownloadSpecDecision.Accept();
            }

            if (!subject.Movie.Monitored)
            {
                _logger.Debug("{0} is present in the DB but not tracked. Rejecting", subject.Movie);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.MovieNotMonitored, "Movie is not monitored");
            }

            // A new edition isn't tracked yet, MovieEditionSpecification decides whether it may be added
            if (subject.MovieEdition is { IsNew: false, Monitored: false })
            {
                _logger.Debug("Edition {0} of {1} is not monitored. Rejecting", subject.MovieEdition, subject.Movie);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.EditionNotMonitored, "Edition is not monitored");
            }

            return DownloadSpecDecision.Accept();
        }
    }
}
