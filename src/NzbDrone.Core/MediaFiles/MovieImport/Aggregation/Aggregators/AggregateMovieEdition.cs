using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators
{
    // Runs after the parsed edition and grab history are known
    public class AggregateMovieEdition : IAggregateLocalMovie
    {
        public int Order => 2;

        private readonly IMovieEditionService _movieEditionService;
        private readonly Logger _logger;

        public AggregateMovieEdition(IMovieEditionService movieEditionService, Logger logger)
        {
            _movieEditionService = movieEditionService;
            _logger = logger;
        }

        public LocalMovie Aggregate(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            if (localMovie.Movie == null || localMovie.MovieEdition != null)
            {
                // Manual import selections are never overridden
                return localMovie;
            }

            var movie = localMovie.Movie;
            var editions = _movieEditionService.GetEditions(movie.Id);
            var match = MovieEditionMatcher.Match(movie.Id, editions, localMovie.Edition);
            var grabbedEditionId = localMovie.Release?.MovieEditionId ?? 0;
            var grabbedEdition = grabbedEditionId > 0 ? editions.FirstOrDefault(e => e.Id == grabbedEditionId) : null;

            if (grabbedEdition != null)
            {
                var parsedExisting = match.Type == MovieEditionMatchType.Matched ||
                                     (match.Type == MovieEditionMatchType.Default && !string.IsNullOrWhiteSpace(localMovie.Edition));

                if (!match.IsResolved)
                {
                    localMovie.MovieEditionRejection = match.Reason;
                }
                else if (parsedExisting && match.Edition.Id != grabbedEdition.Id)
                {
                    localMovie.MovieEditionRejection = $"Grabbed for edition '{grabbedEdition.DisplayTitle}' but the file looks like '{match.Edition.DisplayTitle}'";
                }
                else
                {
                    localMovie.MovieEdition = grabbedEdition;
                }
            }
            else if (match.IsResolved)
            {
                // A new edition is safe to import into: it has no file that could be replaced
                localMovie.MovieEdition = match.Edition;
            }
            else
            {
                localMovie.MovieEditionRejection = match.Reason;
            }

            _logger.Debug("Edition for {0}: {1}", localMovie.Path, localMovie.MovieEdition?.ToString() ?? localMovie.MovieEditionRejection);

            return localMovie;
        }
    }
}
