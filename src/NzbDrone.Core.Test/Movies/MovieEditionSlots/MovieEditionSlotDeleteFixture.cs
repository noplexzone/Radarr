using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    // Feature 11: regression tests for movie delete with/without per-edition files.
    [TestFixture]
    public class MovieEditionSlotDeleteFixture : CoreTest<MovieEditionSlotService>
    {
        private const int MovieId = 99;
        private const int SlotFileId = 55;

        private Movie BuildMovie() => new Movie { Id = MovieId, Path = "/movies/test" };

        private MovieEditionSlot BuildSlot(int? fileId = SlotFileId) => new MovieEditionSlot
        {
            Id = 1,
            MovieId = MovieId,
            EditionName = "Director's Cut",
            Monitored = true,
            MovieFileId = fileId
        };

        [Test]
        public void delete_without_files_removes_slot_records_only()
        {
            var slot = BuildSlot();

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(r => r.FindByMovieId(MovieId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.HandleAsync(new MoviesDeletedEvent(
                new List<Movie> { BuildMovie() },
                deleteFiles: false,
                addImportListExclusion: false));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(r => r.DeleteForMovie(MovieId), Times.Once);
        }

        [Test]
        public void delete_with_files_removes_slot_records()
        {
            var slot = BuildSlot();

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(r => r.FindByMovieId(MovieId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.HandleAsync(new MoviesDeletedEvent(
                new List<Movie> { BuildMovie() },
                deleteFiles: true,
                addImportListExclusion: false));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(r => r.DeleteForMovie(MovieId), Times.Once);
        }

        [Test]
        public void delete_of_movie_file_clears_slot_movie_file_id()
        {
            var slot = BuildSlot(fileId: SlotFileId);

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(r => r.FindByMovieFileId(SlotFileId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.Handle(new MovieFileDeletedEvent(
                new MovieFile { Id = SlotFileId, MovieId = MovieId },
                DeleteMediaFileReason.Manual));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(r => r.Update(It.Is<MovieEditionSlot>(s => s.MovieFileId == null)), Times.Once);
        }

        [Test]
        public void delete_of_unlinked_movie_file_does_not_update_slots()
        {
            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(r => r.FindByMovieFileId(SlotFileId))
                .Returns(new List<MovieEditionSlot>());

            Subject.Handle(new MovieFileDeletedEvent(
                new MovieFile { Id = SlotFileId, MovieId = MovieId },
                DeleteMediaFileReason.Manual));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(r => r.Update(It.IsAny<MovieEditionSlot>()), Times.Never);
        }
    }
}
