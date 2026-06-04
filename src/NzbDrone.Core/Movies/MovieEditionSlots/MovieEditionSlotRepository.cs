using System.Collections.Generic;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionSlotRepository : IBasicRepository<MovieEditionSlot>
    {
        List<MovieEditionSlot> FindByMovieId(int movieId);
        List<MovieEditionSlot> FindByMovieFileId(int movieFileId);
        void DeleteForMovie(int movieId);
    }

    public class MovieEditionSlotRepository : BasicRepository<MovieEditionSlot>, IMovieEditionSlotRepository
    {
        public MovieEditionSlotRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieEditionSlot> FindByMovieId(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieEditionSlot> FindByMovieFileId(int movieFileId)
        {
            return Query(x => x.MovieFileId == movieFileId);
        }

        public void DeleteForMovie(int movieId)
        {
            Delete(x => x.MovieId == movieId);
        }
    }
}
