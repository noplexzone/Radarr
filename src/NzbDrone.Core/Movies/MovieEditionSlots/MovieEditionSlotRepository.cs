using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionSlotRepository : IBasicRepository<MovieEditionSlot>
    {
        List<MovieEditionSlot> FindByMovieId(int movieId);
        List<MovieEditionSlot> FindByIds(IEnumerable<int> ids);
        List<MovieEditionSlot> FindByMovieIds(IEnumerable<int> movieIds);
        bool HasAttachedFile(int slotId);
        void DeleteForMovie(int movieId);
    }

    public interface IMovieEditionSlotAliasRepository : IBasicRepository<MovieEditionSlotAlias>
    {
        List<MovieEditionSlotAlias> FindBySlotIds(IEnumerable<int> slotIds);
        void DeleteForSlot(int slotId);
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
            return slotIds.Count == 0 ? new List<MovieEditionSlot>() : Query(x => slotIds.Contains(x.Id));
        }

        public List<MovieEditionSlot> FindByMovieIds(IEnumerable<int> movieIds)
        {
            var ids = movieIds?.Distinct().ToList() ?? new List<int>();
            return ids.Count == 0 ? new List<MovieEditionSlot>() : Query(x => ids.Contains(x.MovieId));
        }

        public bool HasAttachedFile(int slotId)
        {
            return _database.Query<MovieFile>(new SqlBuilder(_database.DatabaseType)
                .Where<MovieFile>(file => file.MovieEditionSlotId == slotId)).Any();
        }

        public void DeleteForMovie(int movieId)
        {
            Delete(x => x.MovieId == movieId);
        }
    }

    public class MovieEditionSlotAliasRepository : BasicRepository<MovieEditionSlotAlias>, IMovieEditionSlotAliasRepository
    {
        public MovieEditionSlotAliasRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieEditionSlotAlias> FindBySlotIds(IEnumerable<int> slotIds)
        {
            var ids = slotIds?.Distinct().ToList() ?? new List<int>();
            return ids.Count == 0 ? new List<MovieEditionSlotAlias>() : Query(x => ids.Contains(x.MovieEditionSlotId));
        }

        public void DeleteForSlot(int slotId)
        {
            Delete(x => x.MovieEditionSlotId == slotId);
        }
    }
}
