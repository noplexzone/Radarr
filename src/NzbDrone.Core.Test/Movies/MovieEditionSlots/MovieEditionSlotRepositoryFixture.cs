using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionSlotRepositoryFixture : DbTest<MovieEditionSlotRepository, MovieEditionSlot>
    {
        [Test]
        public void find_by_ids_should_return_only_existing_slots()
        {
            var slot = Subject.Insert(new MovieEditionSlot
            {
                MovieId = 12,
                EditionName = "IMAX",
                Monitored = true,
                DateAdded = DateTime.UtcNow
            });

            Subject.FindByIds(new[] { slot.Id, 999 }).Should().ContainSingle(s => s.Id == slot.Id);
        }


        [Test]
        public void has_attached_file_should_use_durable_movie_file_link()
        {
            var slot = Subject.Insert(new MovieEditionSlot
            {
                MovieId = 12,
                EditionName = "Director's Cut",
                CanonicalEditionKey = "directorscut",
                Monitored = true,
                DateAdded = DateTime.UtcNow
            });
            Db.Insert(new MovieFile
            {
                MovieId = 12,
                MovieEditionSlotId = slot.Id,
                Quality = new QualityModel(),
                Languages = new List<Language> { Language.English }
            });

            Subject.HasAttachedFile(slot.Id).Should().BeTrue();
            Subject.HasAttachedFile(slot.Id + 1).Should().BeFalse();
        }

        [Test]
        public void find_by_ids_should_be_empty_safe()
        {
            Subject.FindByIds(new List<int>()).Should().BeEmpty();
        }
    }
}
