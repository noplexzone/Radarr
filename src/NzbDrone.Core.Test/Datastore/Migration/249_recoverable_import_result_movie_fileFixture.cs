using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class recoverable_import_result_movie_fileFixture : MigrationTest<recoverable_import_result_movie_file>
    {
        [Test]
        public void should_add_nullable_result_movie_file_id_after_recoverable_operation_journal()
        {
            using var db = WithDapperMigrationTestDb();
            var column = db.Query<ColumnInfo>(@"PRAGMA table_info('MovieEditionFileOperations')").Single(x => x.Name == "ResultMovieFileId");
            column.NotNull.Should().Be(0);
        }

        private sealed class ColumnInfo
        {
            public string Name { get; set; }
            public int NotNull { get; set; }
        }
    }
}
