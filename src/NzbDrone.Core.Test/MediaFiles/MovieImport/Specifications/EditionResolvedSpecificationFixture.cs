using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Specifications
{
    [TestFixture]
    public class EditionResolvedSpecificationFixture : CoreTest<EditionResolvedSpecification>
    {
        [Test]
        public void should_accept_when_edition_is_known()
        {
            var localMovie = new LocalMovie { MovieEdition = MovieEdition.CreateDefault(1) };

            Subject.IsSatisfiedBy(localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_ambiguous_edition_with_reason()
        {
            var localMovie = new LocalMovie { MovieEditionRejection = "'2in1' contains more than one cut of the movie" };

            var decision = Subject.IsSatisfiedBy(localMovie, null);

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(ImportRejectionReason.AmbiguousEdition);
            decision.Message.Should().Contain("2in1");
        }
    }
}
