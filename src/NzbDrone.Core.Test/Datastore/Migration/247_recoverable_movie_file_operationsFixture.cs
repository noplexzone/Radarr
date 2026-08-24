using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;
namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class recoverable_movie_file_operationsFixture : MigrationTest<recoverable_movie_file_operations>
    {
        [Test] public void should_create_shape_indexes_and_unique_active_resource() { using var db = WithDapperMigrationTestDb(); var columns = db.Query<ColumnInfo>(@"PRAGMA table_info('MovieEditionFileOperations')").Select(x => x.Name); columns.Should().Contain(new[] { "Id", "OperationKey", "ResourceKey", "ActiveResourceKey", "OperationType", "State", "MovieId", "MovieFileId", "MovieEditionSlotId", "Plan", "StagingRoot", "Version", "AttemptCount", "LastAttemptAt", "LastError", "LeaseOwner", "LeaseExpiresAt", "CreatedAt", "UpdatedAt", "DatabaseCommittedAt", "CompletedAt", "EventDispatchMask" }); var indexes = db.Query<IndexInfo>(@"PRAGMA index_list('MovieEditionFileOperations')").ToList(); indexes.Should().Contain(x => x.Name == "UX_MovieEditionFileOperations_OperationKey" && x.Unique == 1); indexes.Should().Contain(x => x.Name == "UX_MovieEditionFileOperations_ActiveResourceKey" && x.Unique == 1); indexes.Should().Contain(x => x.Name == "IX_MovieEditionFileOperations_State_Id"); indexes.Should().Contain(x => x.Name == "IX_MovieEditionFileOperations_MovieId_State_Id"); Insert(db, "one", "movie:1"); Action duplicate = () => Insert(db, "two", "movie:1"); duplicate.Should().Throw<Exception>(); }
        static void Insert(System.Data.IDbConnection db, string key, string resource) { db.Execute(@"INSERT INTO ""MovieEditionFileOperations"" (""OperationKey"",""ResourceKey"",""ActiveResourceKey"",""OperationType"",""State"",""MovieId"",""Plan"",""StagingRoot"",""Version"",""AttemptCount"",""CreatedAt"",""UpdatedAt"",""EventDispatchMask"") VALUES (@key,@resource,@resource,1,1,1,'{}','/movies/1/.radarr-recovery/one/',1,0,@now,@now,0)", new { key, resource, now = DateTime.UtcNow }); }
        sealed class ColumnInfo { public string Name { get; set; } }
        sealed class IndexInfo { public string Name { get; set; } public int Unique { get; set; } }
    }
}
