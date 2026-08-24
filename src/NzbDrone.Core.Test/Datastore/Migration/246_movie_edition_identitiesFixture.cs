using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class movie_edition_identitiesFixture : MigrationTest<movie_edition_identities>
    {
        [Test]
        public void should_backfill_clean_canonical_search_and_alias_identities()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "Director's Cut", "Directors Cut");
                InsertAlias(migration, 100, 10, "Extended Edition");
                InsertAlias(migration, 101, 10, "Filmmaker's Cut");
            });

            var identities = ReadIdentities(db);
            identities.Should().BeEquivalentTo(new[]
            {
                new Identity246 { MovieId = 1, MovieEditionSlotId = 10, NormalizedTerm = "directorscut", IdentityType = 1, DisplayValue = "Director's Cut" },
                new Identity246 { MovieId = 1, MovieEditionSlotId = 10, NormalizedTerm = "extendededition", IdentityType = 3, DisplayValue = "Extended Edition" },
                new Identity246 { MovieId = 1, MovieEditionSlotId = 10, NormalizedTerm = "filmmakerscut", IdentityType = 3, DisplayValue = "Filmmaker's Cut" }
            }, options => options.Excluding(x => x.Id).WithStrictOrdering());
            ReadFindings(db).Should().BeEmpty();
        }

        [Test]
        public void should_deduplicate_same_slot_with_canonical_then_search_then_alias_precedence()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "Director's Cut", "Director-s Cut");
                InsertAlias(migration, 100, 10, "Director’s Cut");
                InsertAlias(migration, 101, 10, "Unique Alias");
                InsertAlias(migration, 102, 10, "Unique-Alias");
            });

            var identities = ReadIdentities(db);
            identities.Should().HaveCount(2);
            identities.Single(x => x.NormalizedTerm == "directorscut").Should().Match<Identity246>(x => x.IdentityType == 1 && x.DisplayValue == "Director's Cut");
            identities.Single(x => x.NormalizedTerm == "uniquealias").Should().Match<Identity246>(x => x.IdentityType == 3 && x.DisplayValue == "Unique Alias");
        }

        [Test]
        public void should_exclude_cross_slot_collision_and_record_every_claimant()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "IMAX", null);
                InsertSlot(migration, 11, 1, "Standard", null);
                InsertAlias(migration, 100, 11, "I‑MAX");
            });

            ReadIdentities(db).Should().NotContain(x => x.NormalizedTerm == "imax");
            var findings = ReadFindings(db).Where(x => x.FindingType == 2 && x.NormalizedTerm == "imax").ToList();
            findings.Should().HaveCount(2);
            findings.Select(x => x.MovieEditionSlotId).Should().Equal(10, 11);
            findings.Select(x => x.IdentityType).Should().Equal(1, 3);
        }

        [Test]
        public void should_use_unicode_normalization()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "Director’s_Cut™ 4K", null);
            });

            ReadIdentities(db).Should().ContainSingle().Which.NormalizedTerm.Should().Be("directorscut4k");
        }

        [Test]
        public void should_record_empty_candidates_and_orphan_aliases()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "Valid", "—_ ™");
                InsertAlias(migration, 100, 10, "$$$");
                InsertAlias(migration, 101, 999, "Orphan Alias");
            });

            ReadIdentities(db).Should().ContainSingle(x => x.NormalizedTerm == "valid");
            var findings = ReadFindings(db);
            findings.Should().Contain(x => x.FindingType == 1 && x.MovieEditionSlotId == 10 && x.IdentityType == 2 && x.DisplayValue == "—_ ™");
            findings.Should().Contain(x => x.FindingType == 1 && x.MovieEditionSlotId == 10 && x.IdentityType == 3 && x.DisplayValue == "$$$");
            findings.Should().Contain(x => x.FindingType == 3 && x.MovieEditionSlotId == 999 && x.DisplayValue == "Orphan Alias");
        }

        [Test]
        public void should_record_and_exclude_slots_whose_movie_is_missing()
        {
            using var db = WithDapperMigrationTestDb(migration => InsertSlot(migration, 10, 999, "Lost Edition", "Lost Search"));

            ReadIdentities(db).Should().BeEmpty();
            ReadFindings(db).Should().ContainSingle(x => x.FindingType == 4 && x.MovieId == 999 && x.MovieEditionSlotId == 10);
        }

        [Test]
        public void should_process_more_than_one_batch_in_stable_order()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                for (var id = 1; id <= 550; id++)
                {
                    InsertSlot(migration, id, 1, $"Edition {id:D4}", null);
                }
            });

            var identities = ReadIdentities(db);
            identities.Should().HaveCount(550);
            identities.Select(x => x.MovieEditionSlotId).Should().Equal(Enumerable.Range(1, 550));
            identities.First().NormalizedTerm.Should().Be("edition0001");
            identities.Last().NormalizedTerm.Should().Be("edition0550");
        }

        [Test]
        public void should_create_unique_movie_normalized_term_index()
        {
            using var db = WithDapperMigrationTestDb(migration =>
            {
                InsertMovie(migration, 1);
                InsertSlot(migration, 10, 1, "IMAX", null);
            });

            Action insertDuplicate = () => db.Execute(@"INSERT INTO ""MovieEditionIdentities"" (""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""IdentityType"", ""DisplayValue"") VALUES (1, 10, 'imax', 3, 'duplicate')");
            insertDuplicate.Should().Throw<Exception>();
        }

        private static List<Identity246> ReadIdentities(IDbConnection db) => db.Query<Identity246>(@"SELECT * FROM ""MovieEditionIdentities"" ORDER BY ""MovieId"", ""MovieEditionSlotId"", ""NormalizedTerm"", ""Id""").ToList();
        private static List<Finding246> ReadFindings(IDbConnection db) => db.Query<Finding246>(@"SELECT * FROM ""MovieEditionIdentityFindings"" ORDER BY ""Id""").ToList();

        private static void InsertMovie(movie_edition_identities migration, int id)
        {
            migration.Insert.IntoTable("Movies").Row(new
            {
                Id = id, MovieMetadataId = 5000 + id, Monitored = true, MinimumAvailability = 1,
                QualityProfileId = 1, Path = $"/movies/{id}", Added = DateTime.UtcNow, Tags = "[]", MovieFileId = 0
            });
        }

        private static void InsertSlot(movie_edition_identities migration, int id, int movieId, string name, string searchTerm)
        {
            migration.Insert.IntoTable("MovieEditionSlots").Row(new
            {
                Id = id, MovieId = movieId, EditionName = name, CanonicalEditionKey = $"fixture-{id}", SearchTerm = searchTerm,
                Monitored = true, QualityProfileId = (int?)null, MinimumCustomFormatScore = (int?)null,
                LastSearchTime = (DateTime?)null, DateAdded = DateTime.UtcNow
            });
        }

        private static void InsertAlias(movie_edition_identities migration, int id, int slotId, string alias)
        {
            migration.Insert.IntoTable("MovieEditionSlotAliases").Row(new
            {
                Id = id, MovieEditionSlotId = slotId, Alias = alias, NormalizedAlias = $"fixture-{id}"
            });
        }
    }

    public class Identity246 : ModelBase
    {
        public int MovieId { get; set; }
        public int MovieEditionSlotId { get; set; }
        public string NormalizedTerm { get; set; }
        public int IdentityType { get; set; }
        public string DisplayValue { get; set; }
    }

    public class Finding246 : ModelBase
    {
        public int FindingType { get; set; }
        public int? MovieId { get; set; }
        public int? MovieEditionSlotId { get; set; }
        public string NormalizedTerm { get; set; }
        public int? IdentityType { get; set; }
        public string DisplayValue { get; set; }
        public string Details { get; set; }
    }
}
