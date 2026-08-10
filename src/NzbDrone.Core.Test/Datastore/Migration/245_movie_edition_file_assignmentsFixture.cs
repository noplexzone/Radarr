using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class movie_edition_file_assignmentsFixture : MigrationTest<movie_edition_file_assignments>
    {
        [Test]
        public void should_migrate_only_deterministic_valid_slot_file_links_and_preserve_every_file()
        {
            var db = WithMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1, 101, 501);
                InsertMovie(migration, 2, 201, 502);
                InsertFile(migration, 101, 1);
                InsertFile(migration, 102, 1);
                InsertFile(migration, 103, 1);
                InsertFile(migration, 201, 2);
                InsertSlot(migration, 10, 1, "Director's Cut", 102);
                InsertSlot(migration, 11, 1, "Extended", 102);
                InsertSlot(migration, 12, 2, "Cross Movie", 103);
                InsertSlot(migration, 13, 1, "Main Pointer", 101);
                InsertSlot(migration, 14, 1, "Missing Pointer", 999);
                InsertSlot(migration, 15, 1, "IMAX", 103);
            });

            var files = db.Query<MovieFile245>("SELECT \"Id\", \"MovieEditionSlotId\" FROM \"MovieFiles\" ORDER BY \"Id\"").ToList();
            files.Should().HaveCount(4);
            files.Single(f => f.Id == 101).MovieEditionSlotId.Should().BeNull();
            files.Single(f => f.Id == 102).MovieEditionSlotId.Should().Be(10);
            files.Single(f => f.Id == 103).MovieEditionSlotId.Should().Be(15);
            files.Single(f => f.Id == 201).MovieEditionSlotId.Should().BeNull();

            var slots = db.Query<MovieEditionSlot245>("SELECT \"Id\", \"CanonicalEditionKey\" FROM \"MovieEditionSlots\" ORDER BY \"Id\"").ToList();
            slots.Single(s => s.Id == 10).CanonicalEditionKey.Should().Be("directorscut");
            slots.Single(s => s.Id == 15).CanonicalEditionKey.Should().Be("imax");
        }

        [Test]
        public void should_add_alias_persistence_table_to_current_slot_schema()
        {
            var db = WithMigrationTestDb(migration => InsertSlot(migration, 1, 1, "Director's Cut", null));
            db.Query("SELECT * FROM \"MovieEditionSlotAliases\"").Should().BeEmpty();
        }

        private static void InsertMovie(movie_edition_file_assignments migration, int id, int mainFileId, int metadataId)
        {
            migration.Insert.IntoTable("Movies").Row(new
            {
                Id = id, MovieMetadataId = metadataId, Monitored = true, MinimumAvailability = 1,
                QualityProfileId = 1, Path = $"/movies/{id}", Added = DateTime.UtcNow, Tags = "[]", MovieFileId = mainFileId
            });
        }

        private static void InsertFile(movie_edition_file_assignments migration, int id, int movieId)
        {
            migration.Insert.IntoTable("MovieFiles").Row(new
            {
                Id = id, MovieId = movieId, RelativePath = $"movie-{id}.mkv", Size = 100L,
                DateAdded = DateTime.UtcNow, Quality = "{\"quality\": 0}", Languages = "[]", IndexerFlags = 0
            });
        }

        private static void InsertSlot(movie_edition_file_assignments migration, int id, int movieId, string name, int? fileId)
        {
            migration.Insert.IntoTable("MovieEditionSlots").Row(new
            {
                Id = id, MovieId = movieId, EditionName = name, SearchTerm = (string)null, Monitored = true,
                MovieFileId = fileId, QualityProfileId = (int?)null, MinimumCustomFormatScore = (int?)null,
                LastSearchTime = (DateTime?)null, DateAdded = DateTime.UtcNow
            });
        }
    }

    public class MovieFile245 : ModelBase { public int? MovieEditionSlotId { get; set; } }
    public class MovieEditionSlot245 : ModelBase { public string CanonicalEditionKey { get; set; } }
    public class MovieEditionSlotAlias245 : ModelBase
    {
        public int MovieEditionSlotId { get; set; }
        public string Alias { get; set; }
        public string NormalizedAlias { get; set; }
    }
}
