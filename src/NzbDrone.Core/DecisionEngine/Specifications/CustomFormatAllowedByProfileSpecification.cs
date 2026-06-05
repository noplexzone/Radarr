using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class CustomFormatAllowedbyProfileSpecification : IDownloadDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public CustomFormatAllowedbyProfileSpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual DownloadSpecDecision IsSatisfiedBy(RemoteMovie subject, SearchCriteriaBase searchCriteria)
        {
            var movieCriteria = searchCriteria as IndexerSearch.Definitions.MovieSearchCriteria;
            var effectiveProfile = movieCriteria?.OverrideQualityProfile ?? subject.Movie.QualityProfile;

            // Slot-level flat override takes priority; otherwise use the effective profile's threshold.
            var minScore = movieCriteria?.SlotMinimumCustomFormatScore ?? effectiveProfile.MinFormatScore;
            var score = subject.CustomFormatScore;

            if (score < minScore)
            {
                return DownloadSpecDecision.Reject(DownloadRejectionReason.CustomFormatMinimumScore, "Custom Formats {0} have score {1} below minimum {2}", subject.CustomFormats.ConcatToString(), score, minScore);
            }

            _logger.Trace("Custom Format Score of {0} [{1}] above minimum {2}", score, subject.CustomFormats.ConcatToString(), minScore);

            return DownloadSpecDecision.Accept();
        }
    }
}
