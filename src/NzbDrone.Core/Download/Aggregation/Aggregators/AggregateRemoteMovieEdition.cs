using System.Linq;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.Aggregation.Aggregators
{
    public class AggregateRemoteMovieEdition : IAggregateRemoteMovie
    {
        private readonly IMovieEditionService _movieEditionService;

        public AggregateRemoteMovieEdition(IMovieEditionService movieEditionService)
        {
            _movieEditionService = movieEditionService;
        }

        public RemoteMovie Aggregate(RemoteMovie remoteMovie)
        {
            var movie = remoteMovie.Movie;

            if (movie == null)
            {
                return remoteMovie;
            }

            var editions = _movieEditionService.GetEditions(movie.Id);

            // An edition restored from grab history or a pending release may have been changed, created or deleted since
            var known = remoteMovie.MovieEdition;
            var current = known == null ? null : known.IsNew ? editions.FirstOrDefault(e => e.Key == known.Key) ?? known : editions.FirstOrDefault(e => e.Id == known.Id);

            if (current != null)
            {
                remoteMovie.MovieEdition = current;
                remoteMovie.MovieEditionMatch ??= current.IsNew ? MovieEditionMatch.New(current) : MovieEditionMatch.Matched(current);
            }
            else
            {
                remoteMovie.MovieEditionMatch = MovieEditionMatcher.Match(movie.Id, editions, remoteMovie.ParsedMovieInfo?.Edition);
                remoteMovie.MovieEdition = remoteMovie.MovieEditionMatch.Edition;
            }

            var edition = remoteMovie.MovieEdition;

            if (edition != null)
            {
                remoteMovie.EditionFile = edition.IsNew ? null : _movieEditionService.GetCurrentFile(movie, edition);
                remoteMovie.QualityProfile = _movieEditionService.GetQualityProfile(movie, edition);
            }

            return remoteMovie;
        }
    }
}
