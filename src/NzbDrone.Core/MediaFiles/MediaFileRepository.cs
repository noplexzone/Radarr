using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public interface IMediaFileRepository : IBasicRepository<MovieFile>
    {
        List<MovieFile> GetFilesByMovie(int movieId);
        List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds);
        MovieFile FindByEditionSlotId(int movieEditionSlotId);
        List<MovieFile> GetFilesByEditionSlotIds(IEnumerable<int> movieEditionSlotIds);
        List<MovieFile> GetUnassignedFiles(int movieId);
        List<MovieFile> GetFilesWithoutMediaInfo();
        void DeleteForMovies(List<int> movieIds);

        List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath);
    }

    public class MediaFileRepository : BasicRepository<MovieFile>, IMediaFileRepository
    {
        public MediaFileRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<MovieFile> GetFilesByMovie(int movieId)
        {
            return Query(x => x.MovieId == movieId);
        }

        public List<MovieFile> GetFilesByMovies(IEnumerable<int> movieIds)
        {
            return Query(x => movieIds.Contains(x.MovieId));
        }

        public MovieFile FindByEditionSlotId(int movieEditionSlotId)
        {
            return Query(x => x.MovieEditionSlotId == movieEditionSlotId).SingleOrDefault();
        }

        public List<MovieFile> GetFilesByEditionSlotIds(IEnumerable<int> movieEditionSlotIds)
        {
            var ids = movieEditionSlotIds?.Distinct().Select(id => (int?)id).ToList() ?? new List<int?>();
            return ids.Count == 0 ? new List<MovieFile>() : Query(x => ids.Contains(x.MovieEditionSlotId));
        }

        public List<MovieFile> GetUnassignedFiles(int movieId)
        {
            var builder = Builder()
                .LeftJoin<MovieFile, Movies.Movie>((file, movie) => file.MovieId == movie.Id)
                .Where<MovieFile>(file => file.MovieId == movieId && file.MovieEditionSlotId == null)
                .Where("\"MovieFiles\".\"Id\" != \"Movies\".\"MovieFileId\"", new { });

            return Query(builder);
        }

        public List<MovieFile> GetFilesWithoutMediaInfo()
        {
            return Query(x => x.MediaInfo == null);
        }

        public void DeleteForMovies(List<int> movieIds)
        {
            Delete(x => movieIds.Contains(x.MovieId));
        }

        public List<MovieFile> GetFilesWithRelativePath(int movieId, string relativePath)
        {
            return Query(c => c.MovieId == movieId && c.RelativePath == relativePath);
        }
    }
}
