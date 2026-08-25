using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(249)]
    public class recoverable_import_result_movie_file : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Alter.Table("MovieEditionFileOperations")
                .AddColumn("ResultMovieFileId").AsInt32().Nullable();
        }
    }
}
