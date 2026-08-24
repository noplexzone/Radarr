using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionIdentityRepositoryContractFixture
    {
        [TestCase(typeof(IMovieEditionIdentityRepository))]
        [TestCase(typeof(IMovieEditionIdentityFindingRepository))]
        public void should_expose_only_read_operations(Type repositoryType)
        {
            var forbidden = new[] { "Insert", "InsertMany", "Update", "UpdateMany", "Upsert", "SetFields", "Delete", "DeleteMany", "Purge" };

            repositoryType.GetMethods().Select(x => x.Name).Should().NotIntersectWith(forbidden);
        }
    }

    [TestFixture]
    public class MovieEditionIdentityRepositoryFixture : DbTest<MovieEditionIdentityRepository, MovieEditionIdentity>
    {
        [Test]
        public void should_query_identities_by_movie_and_slot()
        {
            Storage.Insert(new MovieEditionIdentity { MovieId = 1, MovieEditionSlotId = 10, NormalizedTerm = "imax", IdentityType = MovieEditionIdentityType.CanonicalName, DisplayValue = "IMAX" });
            Storage.Insert(new MovieEditionIdentity { MovieId = 1, MovieEditionSlotId = 11, NormalizedTerm = "extended", IdentityType = MovieEditionIdentityType.SearchTerm, DisplayValue = "Extended" });
            Storage.Insert(new MovieEditionIdentity { MovieId = 2, MovieEditionSlotId = 20, NormalizedTerm = "three-d", IdentityType = MovieEditionIdentityType.Alias, DisplayValue = "Three D" });

            Subject.FindByMovieId(1).Should().HaveCount(2).And.OnlyContain(x => x.MovieId == 1);
            Subject.FindBySlotId(11).Should().ContainSingle(x => x.NormalizedTerm == "extended");
        }
    }

    [TestFixture]
    public class MovieEditionIdentityFindingRepositoryFixture : DbTest<MovieEditionIdentityFindingRepository, MovieEditionIdentityFinding>
    {
        [Test]
        public void should_query_findings_by_movie_slot_and_normalized_term()
        {
            Storage.Insert(new MovieEditionIdentityFinding { FindingType = MovieEditionIdentityFindingType.CrossSlotCollision, MovieId = 1, MovieEditionSlotId = 10, NormalizedTerm = "imax", IdentityType = MovieEditionIdentityType.CanonicalName, DisplayValue = "IMAX", Details = "collision" });
            Storage.Insert(new MovieEditionIdentityFinding { FindingType = MovieEditionIdentityFindingType.CrossSlotCollision, MovieId = 1, MovieEditionSlotId = 11, NormalizedTerm = "imax", IdentityType = MovieEditionIdentityType.Alias, DisplayValue = "I-MAX", Details = "collision" });
            Storage.Insert(new MovieEditionIdentityFinding { FindingType = MovieEditionIdentityFindingType.OrphanAlias, MovieEditionSlotId = 999, NormalizedTerm = "orphan", IdentityType = MovieEditionIdentityType.Alias, DisplayValue = "Orphan", Details = "orphan" });

            Subject.FindByMovieId(1).Should().HaveCount(2);
            Subject.FindBySlotId(11).Should().ContainSingle();
            Subject.FindByNormalizedTerm("imax").Should().HaveCount(2);
        }
    }
}
