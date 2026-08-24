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
    public class recoverable_operation_resourcesFixture : MigrationTest<recoverable_operation_resources>
    {
        [Test]
        public void should_create_resource_reservations_with_unique_key_and_cascade_fk()
        {
            using var db = WithDapperMigrationTestDb();
            var columns = db.Query<ColumnInfo>(@"PRAGMA table_info('MovieEditionFileOperationResources')").ToList();
            columns.Select(x => x.Name).Should().Contain(new[] { "Id", "OperationId", "ResourceKey" });
            columns.Single(x => x.Name == "OperationId").NotNull.Should().Be(1);
            columns.Single(x => x.Name == "ResourceKey").NotNull.Should().Be(1);
            var indexes = db.Query<IndexInfo>(@"PRAGMA index_list('MovieEditionFileOperationResources')").ToList();
            indexes.Should().Contain(x => x.Name == "UX_MovieEditionFileOperationResources_ResourceKey" && x.Unique == 1);
            indexes.Should().Contain(x => x.Name == "IX_MovieEditionFileOperationResources_OperationId_ResourceKey");
            var foreignKeys = db.Query<ForeignKeyInfo>(@"SELECT ""table"" AS ""Table"", ""from"" AS ""From"", ""to"" AS ""To"", ""on_delete"" AS ""OnDelete"" FROM pragma_foreign_key_list('MovieEditionFileOperationResources')").ToList();
            foreignKeys.Should().ContainSingle(x => x.Table == "MovieEditionFileOperations" && x.From == "OperationId" && x.To == "Id" && x.OnDelete == "CASCADE");
        }

        [Test]
        public void should_normalize_and_backfill_only_active_resources_without_rewriting_history()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                Insert(migration, "active", " historical:active ", " historical:active ", 1);
                Insert(migration, "completed", " historical:completed ", null, 7);
                Insert(migration, "blank-active", " historical:blank ", "   ", 1);
            });

            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionFileOperations""").Should().Be(3);
            db.QuerySingle<string>(@"SELECT ""ResourceKey"" FROM ""MovieEditionFileOperationResources""").Should().Be("historical:active");
            db.QuerySingle<string>(@"SELECT ""ActiveResourceKey"" FROM ""MovieEditionFileOperations"" WHERE ""OperationKey""='active'").Should().Be(" historical:active ");
            db.QuerySingle<string>(@"SELECT ""ResourceKey"" FROM ""MovieEditionFileOperations"" WHERE ""OperationKey""='completed'").Should().Be(" historical:completed ");
        }

        [Test]
        public void should_quarantine_normalized_historical_collisions_and_keep_a_guardian_reservation()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                Insert(migration, "collision-one", "\tmovie:1\t", "\tmovie:1\t", 1);
                Insert(migration, "collision-two", " movie:1 ", " movie:1 ", 2);
            });

            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionFileOperations""").Should().Be(2);
            db.Query<int>(@"SELECT ""State"" FROM ""MovieEditionFileOperations"" ORDER BY ""Id""").Should().OnlyContain(state => state == 10);
            db.QuerySingle<string>(@"SELECT ""ResourceKey"" FROM ""MovieEditionFileOperationResources""").Should().Be("movie:1");
            db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""MovieEditionFileOperationResources""").Should().Be(1);
        }

        private static void Insert(recoverable_operation_resources migration, string operationKey, string resourceKey, string activeResourceKey, int state)
        {
            migration.Insert.IntoTable("MovieEditionFileOperations").Row(new
            {
                OperationKey = operationKey,
                ResourceKey = resourceKey,
                ActiveResourceKey = activeResourceKey,
                OperationType = 1,
                State = state,
                MovieId = 1,
                Plan = "{}",
                StagingRoot = $"/movies/1/.radarr-recovery/{operationKey}/",
                Version = 1,
                AttemptCount = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                EventDispatchMask = 0L
            });
        }

        private sealed class ColumnInfo { public string Name { get; set; } public int NotNull { get; set; } }
        private sealed class IndexInfo { public string Name { get; set; } public int Unique { get; set; } }
        private sealed class ForeignKeyInfo { public string Table { get; set; } public string From { get; set; } public string To { get; set; } public string OnDelete { get; set; } }
    }
}
