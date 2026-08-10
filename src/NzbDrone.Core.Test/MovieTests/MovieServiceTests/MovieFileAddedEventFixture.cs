using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.MovieServiceTests
{
    [TestFixture]
    public class MovieFileAddedEventFixture : CoreTest<MovieService>
    {
        [Test]
        public void should_assign_only_a_file_explicitly_stamped_as_main()
        {
            var movie = new Movie();
            var file = new MovieFile { Id = 10, Movie = movie, ImportTarget = MovieFileImportTarget.Main };

            Subject.Handle(new MovieFileAddedEvent(file));

            movie.MovieFileId.Should().Be(file.Id);
            Mocker.GetMock<IMovieRepository>().Verify(r => r.Update(movie), Times.Once);
        }

        [TestCase(null)]
        [TestCase(20)]
        public void should_not_assign_unassigned_or_slot_file_as_main(int? slotId)
        {
            var movie = new Movie();
            var file = new MovieFile
            {
                Id = 10,
                Movie = movie,
                MovieEditionSlotId = slotId,
                ImportTarget = slotId.HasValue ? MovieFileImportTarget.EditionSlot : MovieFileImportTarget.Unassigned
            };

            Subject.Handle(new MovieFileAddedEvent(file));

            movie.MovieFileId.Should().Be(0);
            Mocker.GetMock<IMovieRepository>().Verify(r => r.Update(It.IsAny<Movie>()), Times.Never);
        }
    }
}
