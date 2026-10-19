using NLog;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
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

        public DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, ReleaseDecisionInformation information)
        {
            var match = subject.MovieEditionMatch;
            var edition = subject.MovieEdition;
            var searchCriteria = information.SearchCriteria;

            if (match == null && edition == null)
            {
                return DownloadSpecDecision.Accept();
            }

            if (edition == null)
            {
                _logger.Debug("Edition of {0} could not be determined: {1}", subject.Release?.Title, match.Reason);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.EditionAmbiguous, "Edition could not be determined: {0}", match.Reason);
            }

            if (searchCriteria is { MovieEditionId: > 0 } && edition.Id != searchCriteria.MovieEditionId)
            {
                _logger.Debug("Release is for edition {0}, not the edition searched for", edition);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.WrongEdition, "Release is for edition {0}, not the edition searched for", edition.DisplayTitle);
            }

            if (edition.IsNew && !subject.Movie.AutoAddEditions && searchCriteria is not { InteractiveSearch: true })
            {
                _logger.Debug("Release is for edition {0} which {1} doesn't have, and new editions aren't added automatically", edition, subject.Movie);
                return DownloadSpecDecision.Reject(DownloadRejectionReason.EditionNotTracked, "New edition: {0}, not tracked", edition.DisplayTitle);
            }

            return DownloadSpecDecision.Accept();
        }
    }
}
