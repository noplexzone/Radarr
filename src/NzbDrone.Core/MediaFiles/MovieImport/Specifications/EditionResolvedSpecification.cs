using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    public class EditionResolvedSpecification : IImportDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public EditionResolvedSpecification(Logger logger)
        {
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (localMovie.MovieEdition != null)
            {
                return ImportSpecDecision.Accept();
            }

            var reason = localMovie.MovieEditionRejection ?? "Edition could not be determined";

            _logger.Debug("Edition could not be determined safely for {0}: {1}", localMovie.Path, reason);

            return ImportSpecDecision.Reject(ImportRejectionReason.AmbiguousEdition, "{0}, use Manual Import to choose the edition", reason);
        }
    }
}
