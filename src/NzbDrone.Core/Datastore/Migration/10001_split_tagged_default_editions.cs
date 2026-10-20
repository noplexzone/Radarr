using System;
using System.Data;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    // 10000 made a movie's only file the default edition and named the edition after the file's tag,
    // so a movie with just "Remastered" had no Standard edition. That edition now becomes a normal
    // edition keeping its file, and the movie gets an empty Standard default edition. It starts
    // unmonitored so the change doesn't start any searches.
    [Migration(10001)]
    public class split_tagged_default_editions : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Execute.WithConnection(SplitTaggedDefaults);
        }

        private void SplitTaggedDefaults(IDbConnection conn, IDbTransaction tran)
        {
            var tagged = conn.Query<Edition10001>("SELECT \"Id\", \"MovieId\" FROM \"MovieEditions\" WHERE \"IsDefault\" = @isDefault AND \"Key\" <> ''",
                                                  new { isDefault = true },
                                                  transaction: tran)
                .ToList();

            if (!tagged.Any())
            {
                return;
            }

            conn.Execute("UPDATE \"MovieEditions\" SET \"IsDefault\" = @isDefault WHERE \"Id\" = @Id",
                         tagged.Select(e => new { e.Id, isDefault = false }),
                         transaction: tran);

            var now = DateTime.UtcNow;

            conn.Execute("INSERT INTO \"MovieEditions\" (\"MovieId\", \"Title\", \"Key\", \"IsDefault\", \"Monitored\", \"QualityProfileId\", \"Added\") " +
                         "VALUES (@MovieId, NULL, '', @isDefault, @monitored, 0, @added)",
                         tagged.Select(e => new { e.MovieId, isDefault = true, monitored = false, added = now }),
                         transaction: tran);
        }

        private class Edition10001
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
        }
    }
}
