using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedMovieFiles : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedMovieFiles(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            // A movie can have a file for each of its editions, only its main one is Movies.MovieFileId,
            // so a file is orphaned when its movie is gone rather than when no movie points at it
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""MovieFiles""
                             WHERE ""Id"" IN (
                             SELECT ""MovieFiles"".""Id"" FROM ""MovieFiles""
                             LEFT OUTER JOIN ""Movies""
                             ON ""MovieFiles"".""MovieId"" = ""Movies"".""Id""
                             WHERE ""Movies"".""Id"" IS NULL)");
        }
    }
}
