using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies.Editions
{
    public interface IMovieEditionRepository : IBasicRepository<MovieEdition>
    {
        List<MovieEdition> GetByMovieId(int movieId);
        List<MovieEdition> GetByMovieIds(IEnumerable<int> movieIds);
        void DeleteForMovies(List<int> movieIds);
        bool AnyMovieHasMultipleEditions();
    }

    public class MovieEditionRepository : BasicRepository<MovieEdition>, IMovieEditionRepository
    {
        public MovieEditionRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        protected override bool PublishModelEvents => true;

        public List<MovieEdition> GetByMovieId(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieEdition> GetByMovieIds(IEnumerable<int> movieIds)
        {
            return Query(x => movieIds.Contains(x.MovieId));
        }

        public void DeleteForMovies(List<int> movieIds)
        {
            Delete(x => movieIds.Contains(x.MovieId));
        }

        public bool AnyMovieHasMultipleEditions()
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<int>("SELECT \"MovieId\" FROM \"MovieEditions\" GROUP BY \"MovieId\" HAVING COUNT(*) > 1 LIMIT 1").Any();
            }
        }
    }
}
