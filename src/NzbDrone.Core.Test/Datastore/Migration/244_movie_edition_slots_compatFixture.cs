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
    public class movie_edition_slots_compatFixture : MigrationTest<movie_edition_slots_compat>
    {
        [Test]
        public void should_create_slots_for_a_clean_pre_feature_schema()
        {
            var db = WithMigrationTestDb();
            db.Query("SELECT * FROM \"MovieEditionSlots\"").Should().BeEmpty();
        }

        [Test]
        public void should_preserve_populated_prototype_values_before_dropping_columns()
        {
            var db = WithMigrationTestDb(migration =>
            {
                migration.Delete.Index("IX_Movies_MovieMetadataId").OnTable("Movies");
                migration.Alter.Table("Movies")
                    .AddColumn("MovieEdition").AsString().Nullable()
                    .AddColumn("EditionSearchTerm").AsString().Nullable();
                migration.Create.Index("IX_Movies_MovieMetadataId_MovieEdition")
                    .OnTable("Movies")
                    .OnColumn("MovieMetadataId").Ascending()
                    .OnColumn("MovieEdition").Ascending()
                    .WithOptions().Unique();
                InsertMovie(migration, 1, 101, 501, "Director's Cut", "Directors Cut");
                InsertMovie(migration, 2, 0, 502, null, "ignored");
            });

            var slots = db.Query<MovieEditionSlot244>("SELECT \"MovieId\", \"EditionName\", \"SearchTerm\", \"MovieFileId\" FROM \"MovieEditionSlots\"").ToList();
            slots.Should().ContainSingle();
            slots[0].MovieId.Should().Be(1);
            slots[0].EditionName.Should().Be("Director's Cut");
            slots[0].SearchTerm.Should().Be("Directors Cut");
            slots[0].MovieFileId.Should().BeNull();
            db.Query("SELECT * FROM \"Movies\"").Should().HaveCount(2);
            db.Query("SELECT * FROM \"MovieFiles\"").Should().ContainSingle();
        }

        private static void InsertMovie(movie_edition_slots_compat migration, int id, int movieFileId, int metadataId, string edition, string searchTerm)
        {
            if (movieFileId > 0)
            {
                migration.Insert.IntoTable("MovieFiles").Row(new
                {
                    Id = movieFileId, MovieId = id, RelativePath = $"movie-{id}.mkv", Size = 100L,
                    DateAdded = DateTime.UtcNow, Quality = "{\"quality\": 0}", Languages = "[]", IndexerFlags = 0
                });
            }

            migration.Insert.IntoTable("Movies").Row(new
            {
                Id = id, MovieMetadataId = metadataId, Monitored = true, MinimumAvailability = 1,
                QualityProfileId = 1, Path = $"/movies/{id}", Added = DateTime.UtcNow, Tags = "[]",
                MovieFileId = movieFileId, MovieEdition = edition, EditionSearchTerm = searchTerm
            });
        }
    }

    public class MovieEditionSlot244 : ModelBase
    {
        public int MovieId { get; set; }
        public string EditionName { get; set; }
        public string SearchTerm { get; set; }
        public int? MovieFileId { get; set; }
    }
}
