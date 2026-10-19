using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    // Fork migrations are numbered from 10000, so a future upstream migration will have a lower number
    // than migrations already applied. It still has to run when the fork is rebased onto that release.
    [TestFixture]
    public class fork_migration_orderFixture : DbTest
    {
        [Test]
        public void upstream_migration_numbered_below_fork_migrations_should_still_run()
        {
            var db = Mocker.Resolve<IMainDatabase>();

            // Pretend upstream migration 242 arrives after the fork's migrations were applied
            using (var conn = db.OpenConnection())
            {
                conn.Execute("DELETE FROM \"VersionInfo\" WHERE \"Version\" = 242");
                conn.Execute("ALTER TABLE \"MovieMetadata\" DROP COLUMN \"Keywords\"");
                conn.Execute("INSERT INTO \"MovieEditions\" (\"MovieId\", \"Title\", \"Key\", \"IsDefault\", \"Monitored\", \"QualityProfileId\", \"Added\") " +
                             "VALUES (1, 'Final Cut', 'finalcut', @isDefault, @monitored, 0, @added)",
                             new { isDefault = false, monitored = true, added = System.DateTime.UtcNow });
            }

            var connectionInfo = Mocker.Resolve<IConnectionStringFactory>().MainDbConnection;
            Mocker.Resolve<IMigrationController>().Migrate(connectionInfo.ConnectionString, new MigrationContext(MigrationType.Main), connectionInfo.DatabaseType);

            using (var conn = db.OpenConnection())
            {
                var versions = conn.Query<long>("SELECT \"Version\" FROM \"VersionInfo\"").ToList();

                versions.Should().Contain(242);
                versions.Should().ContainSingle(v => v == 10000);

                // The re-run migration added its column back and the fork's data is untouched
                conn.Query<string>("SELECT \"Keywords\" FROM \"MovieMetadata\"").Should().BeEmpty();
                conn.Query<string>("SELECT \"Title\" FROM \"MovieEditions\"").Should().ContainSingle(t => t == "Final Cut");
            }
        }
    }
}
