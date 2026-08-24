using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionIdentityRepository
    {
        List<MovieEditionIdentity> FindByMovieId(int movieId);
        List<MovieEditionIdentity> FindBySlotId(int movieEditionSlotId);
    }

    public interface IMovieEditionIdentityFindingRepository
    {
        List<MovieEditionIdentityFinding> FindByMovieId(int movieId);
        List<MovieEditionIdentityFinding> FindBySlotId(int movieEditionSlotId);
        List<MovieEditionIdentityFinding> FindByNormalizedTerm(string normalizedTerm);
    }

    public class MovieEditionIdentityRepository : BasicRepository<MovieEditionIdentity>, IMovieEditionIdentityRepository
    {
        public MovieEditionIdentityRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieEditionIdentity> FindByMovieId(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieEditionIdentity> FindBySlotId(int movieEditionSlotId)
        {
            return Query(x => x.MovieEditionSlotId == movieEditionSlotId);
        }
    }

    public class MovieEditionIdentityFindingRepository : BasicRepository<MovieEditionIdentityFinding>, IMovieEditionIdentityFindingRepository
    {
        public MovieEditionIdentityFindingRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieEditionIdentityFinding> FindByMovieId(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieEditionIdentityFinding> FindBySlotId(int movieEditionSlotId)
        {
            return Query(x => x.MovieEditionSlotId == movieEditionSlotId);
        }

        public List<MovieEditionIdentityFinding> FindByNormalizedTerm(string normalizedTerm)
        {
            return Query(x => x.NormalizedTerm == normalizedTerm);
        }
    }
}
