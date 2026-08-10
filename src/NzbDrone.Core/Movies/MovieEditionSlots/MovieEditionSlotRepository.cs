using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionSlotRepository : IBasicRepository<MovieEditionSlot>
    {
        List<MovieEditionSlot> FindByMovieId(int movieId);
        List<MovieEditionSlot> FindByIds(IEnumerable<int> ids);
        List<MovieEditionSlot> FindByMovieIds(IEnumerable<int> movieIds);
        List<MovieEditionSlot> FindByMovieFileId(int movieFileId);
        List<MovieEditionSlot> FindMonitoredWithoutFiles();
        List<MovieEditionSlot> FindMonitoredWithFiles();
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

        public List<MovieEditionSlot> FindByIds(IEnumerable<int> ids)
        {
            var slotIds = ids?.Distinct().ToList() ?? new List<int>();
            if (slotIds.Count == 0)
            {
                return new List<MovieEditionSlot>();
            }

            return Query(x => slotIds.Contains(x.Id));
        }

        public List<MovieEditionSlot> FindByMovieIds(IEnumerable<int> movieIds)
        {
            var ids = movieIds.ToList();
            if (ids.Count == 0)
            {
                return new List<MovieEditionSlot>();
            }

            return Query(x => ids.Contains(x.MovieId));
        }

        public List<MovieEditionSlot> FindByMovieFileId(int movieFileId)
        {
            return Query(x => x.MovieFileId == movieFileId);
        }

        public List<MovieEditionSlot> FindMonitoredWithoutFiles()
        {
            return Query(x => x.Monitored && x.MovieFileId == null);
        }

        public List<MovieEditionSlot> FindMonitoredWithFiles()
        {
            return Query(x => x.Monitored && x.MovieFileId != null);
        }

        public void DeleteForMovie(int movieId)
        {
            Delete(x => x.MovieId == movieId);
        }
    }
}
