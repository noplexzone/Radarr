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
        List<int> GetMovieIdsWithMissingEditions();
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

        // Monitored movies with a monitored edition that has no file, untagged files count for the default edition
        public List<int> GetMovieIdsWithMissingEditions()
        {
            using (var conn = _database.OpenConnection())
            {
                return conn.Query<int>("SELECT DISTINCT \"MovieEditions\".\"MovieId\" FROM \"MovieEditions\" " +
                                       "JOIN \"Movies\" ON \"Movies\".\"Id\" = \"MovieEditions\".\"MovieId\" " +
                                       "WHERE \"Movies\".\"Monitored\" = @Monitored AND \"MovieEditions\".\"Monitored\" = @Monitored " +
                                       "AND NOT EXISTS (SELECT 1 FROM \"MovieFiles\" WHERE \"MovieFiles\".\"MovieId\" = \"MovieEditions\".\"MovieId\" " +
                                       "AND (\"MovieFiles\".\"MovieEditionId\" = \"MovieEditions\".\"Id\" OR (\"MovieEditions\".\"IsDefault\" = @Monitored AND \"MovieFiles\".\"MovieEditionId\" = 0)))",
                                       new { Monitored = true }).ToList();
            }
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
