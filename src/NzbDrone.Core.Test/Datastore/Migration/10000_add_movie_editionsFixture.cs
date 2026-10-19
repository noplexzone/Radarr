using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class add_movie_editionsFixture : MigrationTest<add_movie_editions>
    {
        private void AddMovie(add_movie_editions c, int id, int movieFileId)
        {
            // Ids are generated in insertion order, so callers add movies in id order
            c.Insert.IntoTable("Movies").Row(new
            {
                Monitored = true,
                MinimumAvailability = 4,
                QualityProfileId = 1,
                MovieFileId = movieFileId,
                MovieMetadataId = id,
                Path = $"/Movies/Title {id}",
                Added = DateTime.UtcNow,
                Tags = new List<int>().ToJson()
            });
        }

        private void AddFile(add_movie_editions c, int id, int movieId, string edition, long size = 1000)
        {
            c.Insert.IntoTable("MovieFiles").Row(new
            {
                MovieId = movieId,
                RelativePath = $"Title {id}.mkv",
                Quality = new { }.ToJson(),
                IndexerFlags = 0,
                Size = size,
                DateAdded = DateTime.UtcNow,
                Languages = new List<int> { 1 }.ToJson(),
                Edition = edition
            });
        }

        [Test]
        public void should_create_monitored_default_edition_for_movie_without_files()
        {
            var db = WithDapperMigrationTestDb(c => AddMovie(c, 1, 0));

            var editions = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").ToList();

            editions.Should().HaveCount(1);
            editions.First().MovieId.Should().Be(1);
            editions.First().IsDefault.Should().BeTrue();
            editions.First().Monitored.Should().BeTrue();
            editions.First().Title.Should().BeNull();
            editions.First().Key.Should().BeEmpty();
        }

        [Test]
        public void should_put_untagged_single_file_in_default_edition()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 1);
                AddFile(c, 1, 1, null);
            });

            var edition = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").Single();
            var file = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").Single();

            edition.IsDefault.Should().BeTrue();
            file.MovieEditionId.Should().Be(edition.Id);
            db.Query<int>("SELECT \"MovieFileId\" FROM \"Movies\"").Single().Should().Be(1);
        }

        [Test]
        public void should_make_tagged_single_file_the_default_edition()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 1);
                AddFile(c, 1, 1, "Final Cut");
            });

            var edition = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").Single();
            var file = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").Single();

            edition.IsDefault.Should().BeTrue();
            edition.Monitored.Should().BeTrue();
            edition.Title.Should().Be("Final Cut");
            edition.Key.Should().Be("final");
            file.MovieEditionId.Should().Be(edition.Id);
        }

        [Test]
        public void should_split_multiple_files_into_editions()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 3);
                AddFile(c, 1, 1, "Theatrical");
                AddFile(c, 2, 1, "Director's Cut");
                AddFile(c, 3, 1, "Final Cut");
            });

            var editions = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").ToList();
            var files = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").ToList();

            editions.Should().HaveCount(4);

            var defaultEdition = editions.Single(e => e.IsDefault);
            defaultEdition.Monitored.Should().BeFalse();
            files.Should().NotContain(f => f.MovieEditionId == defaultEdition.Id);

            editions.Where(e => !e.IsDefault).Select(e => e.Title).Should().BeEquivalentTo("Theatrical", "Director's Cut", "Final Cut");
            editions.Where(e => !e.IsDefault).Should().OnlyContain(e => e.Monitored);

            files.Select(f => f.MovieEditionId).Distinct().Should().HaveCount(3);

            // The existing pointer stays put when the default edition has no file
            db.Query<int>("SELECT \"MovieFileId\" FROM \"Movies\"").Single().Should().Be(3);
        }

        [Test]
        public void should_put_untagged_file_in_default_edition_next_to_tagged_files()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 2);
                AddFile(c, 1, 1, null);
                AddFile(c, 2, 1, "Extended Edition");
            });

            var editions = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").ToList();
            var files = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").ToList();

            var defaultEdition = editions.Single(e => e.IsDefault);
            defaultEdition.Monitored.Should().BeTrue();

            files.Single(f => f.Id == 1).MovieEditionId.Should().Be(defaultEdition.Id);
            files.Single(f => f.Id == 2).MovieEditionId.Should().Be(editions.Single(e => e.Key == "extended").Id);

            // The movie's main file is moved to the default edition's file
            db.Query<int>("SELECT \"MovieFileId\" FROM \"Movies\"").Single().Should().Be(1);
        }

        [Test]
        public void should_group_spelling_variants_into_one_edition()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 1);
                AddFile(c, 1, 1, "Directors Cut");
                AddFile(c, 2, 1, "Director's Cut");
            });

            var editions = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").ToList();
            var files = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").ToList();

            editions.Should().HaveCount(2);
            files.Select(f => f.MovieEditionId).Distinct().Should().ContainSingle()
                .Which.Should().Be(editions.Single(e => e.Key == "director's").Id);
        }

        [Test]
        public void should_keep_movies_separate()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddMovie(c, 1, 1);
                AddMovie(c, 2, 2);
                AddFile(c, 1, 1, "Unrated");
                AddFile(c, 2, 2, "Unrated");
            });

            var editions = db.Query<MovieEdition10000>("SELECT * FROM \"MovieEditions\"").ToList();
            var files = db.Query<MovieFile10000>("SELECT * FROM \"MovieFiles\"").ToList();

            editions.Should().HaveCount(2);
            files.Single(f => f.Id == 1).MovieEditionId.Should().Be(editions.Single(e => e.MovieId == 1).Id);
            files.Single(f => f.Id == 2).MovieEditionId.Should().Be(editions.Single(e => e.MovieId == 2).Id);
        }

        [Test]
        public void should_default_auto_add_editions_to_false()
        {
            var db = WithDapperMigrationTestDb(c => AddMovie(c, 1, 0));

            db.Query<bool>("SELECT \"AutoAddEditions\" FROM \"Movies\"").Single().Should().BeFalse();
        }

        private class MovieEdition10000
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public string Title { get; set; }
            public string Key { get; set; }
            public bool IsDefault { get; set; }
            public bool Monitored { get; set; }
            public int QualityProfileId { get; set; }
        }

        private class MovieFile10000
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public int MovieEditionId { get; set; }
        }
    }
}
