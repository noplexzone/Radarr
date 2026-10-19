using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport.Specifications
{
    public class UpgradeSpecification : IImportDecisionEngineSpecification
    {
        private readonly IConfigService _configService;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IMovieEditionService _movieEditionService;
        private readonly Logger _logger;

        public UpgradeSpecification(IConfigService configService,
                                    ICustomFormatCalculationService formatService,
                                    IMovieEditionService movieEditionService,
                                    Logger logger)
        {
            _configService = configService;
            _formatService = formatService;
            _movieEditionService = movieEditionService;
            _logger = logger;
        }

        public ImportSpecDecision IsSatisfiedBy(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var downloadPropersAndRepacks = _configService.DownloadPropersAndRepacks;
            var edition = localMovie.MovieEdition;
            var qualityProfile = edition == null ? localMovie.Movie.QualityProfile : _movieEditionService.GetQualityProfile(localMovie.Movie, edition);
            var qualityComparer = new QualityModelComparer(qualityProfile);

            // Only the file of the edition being imported into is compared, other editions are never affected
            var hasExistingFile = edition == null ? localMovie.Movie.MovieFileId > 0 : !edition.IsNew;

            if (hasExistingFile)
            {
                var movieFile = edition == null ? localMovie.Movie.MovieFile : _movieEditionService.GetCurrentFile(localMovie.Movie, edition);

                if (edition != null && movieFile == null)
                {
                    return ImportSpecDecision.Accept();
                }

                if (edition != null && localMovie.ExistingFile)
                {
                    // A second file of the same edition already in the movie folder is tracked as a duplicate, nothing is replaced
                    _logger.Debug("{0} is another file of edition {1} already in the movie folder", localMovie.Path, edition);
                    return ImportSpecDecision.Accept();
                }

                if (movieFile == null)
                {
                    _logger.Trace("Unable to get movie file details from the DB. MovieId: {0} MovieFileId: {1}", localMovie.Movie.Id, localMovie.Movie.MovieFileId);

                    return ImportSpecDecision.Accept();
                }

                var qualityCompare = qualityComparer.Compare(localMovie.Quality.Quality, movieFile.Quality.Quality);

                if (qualityCompare < 0)
                {
                    _logger.Debug("This file isn't a quality upgrade for movie. Existing quality: {0}. New Quality {1}. Skipping {2}", movieFile.Quality.Quality, localMovie.Quality.Quality, localMovie.Path);
                    return ImportSpecDecision.Reject(ImportRejectionReason.NotQualityUpgrade, "Not an upgrade for existing movie file. Existing quality: {0}. New Quality {1}.", movieFile.Quality.Quality, localMovie.Quality.Quality);
                }

                // Same quality, propers/repacks are preferred and it is not a revision update. Reject revision downgrade.

                if (qualityCompare == 0 &&
                    downloadPropersAndRepacks != ProperDownloadTypes.DoNotPrefer &&
                    localMovie.Quality.Revision.CompareTo(movieFile.Quality.Revision) < 0)
                {
                    _logger.Debug("This file isn't a quality revision upgrade for movie. Skipping {0}", localMovie.Path);
                    return ImportSpecDecision.Reject(ImportRejectionReason.NotRevisionUpgrade, "Not a quality revision upgrade for existing movie file(s)");
                }

                movieFile.Movie = localMovie.Movie;
                var currentCustomFormats = _formatService.ParseCustomFormat(movieFile);
                var currentFormatScore = qualityProfile.CalculateCustomFormatScore(currentCustomFormats);
                var newCustomFormats = localMovie.CustomFormats;
                var newFormatScore = localMovie.CustomFormatScore;

                if (qualityCompare == 0 && newFormatScore < currentFormatScore)
                {
                    _logger.Debug("New item's custom formats [{0}] ({1}) do not improve on [{2}] ({3}), skipping",
                        newCustomFormats != null ? newCustomFormats.ConcatToString() : "",
                        newFormatScore,
                        currentCustomFormats != null ? currentCustomFormats.ConcatToString() : "",
                        currentFormatScore);

                    return ImportSpecDecision.Reject(ImportRejectionReason.NotCustomFormatUpgrade,
                        "Not a Custom Format upgrade for existing movie file(s). New: [{0}] ({1}) do not improve on Existing: [{2}] ({3})",
                        newCustomFormats != null ? newCustomFormats.ConcatToString() : "",
                        newFormatScore,
                        currentCustomFormats != null ? currentCustomFormats.ConcatToString() : "",
                        currentFormatScore);
                }

                _logger.Debug("New item's custom formats [{0}] ({1}) do improve on [{2}] ({3}), accepting",
                    newCustomFormats != null ? newCustomFormats.ConcatToString() : "",
                    newFormatScore,
                    currentCustomFormats != null ? currentCustomFormats.ConcatToString() : "",
                    currentFormatScore);
            }

            return ImportSpecDecision.Accept();
        }
    }
}
