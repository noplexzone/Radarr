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
    public class split_tagged_default_editionsFixture : MigrationTest<split_tagged_default_editions>
    {
        private void AddEdition(split_tagged_default_editions c, int movieId, string title, string key, bool isDefault)
        {
            c.Insert.IntoTable("MovieEditions").Row(new
            {
                MovieId = movieId,
                Title = title,
                Key = key,
                IsDefault = isDefault,
                Monitored = true,
                QualityProfileId = 0,
                Added = DateTime.UtcNow
            });
        }

        [Test]
        public void should_add_unmonitored_standard_edition_next_to_tagged_default()
        {
            var db = WithDapperMigrationTestDb(c => AddEdition(c, 1, "Remastered", "remastered", true));

            var editions = db.Query<MovieEdition10001>("SELECT * FROM \"MovieEditions\" ORDER BY \"Id\"").ToList();

            editions.Should().HaveCount(2);

            editions[0].Id.Should().Be(1);
            editions[0].Title.Should().Be("Remastered");
            editions[0].IsDefault.Should().BeFalse();
            editions[0].Monitored.Should().BeTrue();

            editions[1].MovieId.Should().Be(1);
            editions[1].Title.Should().BeNull();
            editions[1].Key.Should().BeEmpty();
            editions[1].IsDefault.Should().BeTrue();
            editions[1].Monitored.Should().BeFalse();
        }

        [Test]
        public void should_leave_standard_default_editions_alone()
        {
            var db = WithDapperMigrationTestDb(c =>
            {
                AddEdition(c, 1, null, string.Empty, true);
                AddEdition(c, 1, "Final Cut", "final", false);
            });

            var editions = db.Query<MovieEdition10001>("SELECT * FROM \"MovieEditions\" ORDER BY \"Id\"").ToList();

            editions.Should().HaveCount(2);
            editions[0].IsDefault.Should().BeTrue();
            editions[0].Monitored.Should().BeTrue();
            editions[1].IsDefault.Should().BeFalse();
        }

        private class MovieEdition10001
        {
            public int Id { get; set; }
            public int MovieId { get; set; }
            public string Title { get; set; }
            public string Key { get; set; }
            public bool IsDefault { get; set; }
            public bool Monitored { get; set; }
        }
    }
}
