using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionSlotDeleteFixture : CoreTest<MovieEditionSlotService>
    {
        [Test]
        public void movie_delete_should_remove_aliases_before_slots()
        {
            const int movieId = 99;
            var slot = new MovieEditionSlot { Id = 1, MovieId = movieId, EditionName = "Director's Cut" };
            Mocker.GetMock<IMovieEditionSlotRepository>().Setup(r => r.FindByMovieId(movieId)).Returns(new List<MovieEditionSlot> { slot });

            Subject.HandleAsync(new MoviesDeletedEvent(
                new List<Movie> { new Movie { Id = movieId, Path = "/movies/test" } },
                deleteFiles: false,
                addImportListExclusion: false));

            Mocker.GetMock<IMovieEditionSlotAliasRepository>().Verify(r => r.DeleteForSlot(slot.Id), Times.Once);
            Mocker.GetMock<IMovieEditionSlotRepository>().Verify(r => r.DeleteForMovie(movieId), Times.Once);
        }
    }
}
