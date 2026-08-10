using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.MovieEditionSlots;
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
        public void find_by_ids_should_be_empty_safe()
        {
            Subject.FindByIds(new List<int>()).Should().BeEmpty();
        }
    }
}
