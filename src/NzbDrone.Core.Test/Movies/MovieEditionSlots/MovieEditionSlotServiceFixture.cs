using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieEditionSlots
{
    [TestFixture]
    public class MovieEditionSlotServiceFixture : CoreTest<MovieEditionSlotService>
    {
        private const int MovieId = 12;
        private const int MovieFileId = 34;

        private MovieFile BuildMovieFile(string edition)
        {
            return new MovieFile
            {
                Id = MovieFileId,
                MovieId = MovieId,
                Edition = edition
            };
        }

        [Test]
        public void should_match_existing_slot_by_edition_name()
        {
            var slot = new MovieEditionSlot
            {
                Id = 1,
                MovieId = MovieId,
                EditionName = "Director's Cut",
                SearchTerm = null
            };

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(s => s.FindByMovieId(MovieId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.Handle(new MovieFileAddedEvent(BuildMovieFile("Directors.Cut")));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(x => x.Id == slot.Id && x.MovieFileId == MovieFileId)), Times.Once);

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Insert(It.IsAny<MovieEditionSlot>()), Times.Never);
        }

        [Test]
        public void should_match_existing_slot_by_search_term()
        {
            var slot = new MovieEditionSlot
            {
                Id = 2,
                MovieId = MovieId,
                EditionName = "IMAX Enhanced",
                SearchTerm = "IMAX"
            };

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(s => s.FindByMovieId(MovieId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.Handle(new MovieFileUpdatedEvent(BuildMovieFile("IMAX")));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(x => x.Id == slot.Id && x.MovieFileId == MovieFileId)), Times.Once);

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Insert(It.IsAny<MovieEditionSlot>()), Times.Never);
        }

        [Test]
        public void should_create_slot_when_imported_edition_has_no_match()
        {
            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(s => s.FindByMovieId(MovieId))
                .Returns(new List<MovieEditionSlot>());

            Subject.Handle(new MovieFileAddedEvent(BuildMovieFile("Extended Edition")));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Insert(It.Is<MovieEditionSlot>(x =>
                    x.MovieId == MovieId &&
                    x.EditionName == "Extended Edition" &&
                    x.SearchTerm == "Extended Edition" &&
                    x.Monitored &&
                    x.MovieFileId == MovieFileId &&
                    x.DateAdded != default)), Times.Once);
        }

        [Test]
        public void should_clear_movie_file_id_when_file_is_deleted()
        {
            var slot = new MovieEditionSlot
            {
                Id = 3,
                MovieId = MovieId,
                EditionName = "IMAX",
                SearchTerm = "IMAX",
                Monitored = true,
                MovieFileId = MovieFileId
            };

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Setup(s => s.FindByMovieFileId(MovieFileId))
                .Returns(new List<MovieEditionSlot> { slot });

            Subject.Handle(new MovieFileDeletedEvent(BuildMovieFile("IMAX"), DeleteMediaFileReason.Manual));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(x => x.Id == slot.Id && x.MovieFileId == null)), Times.Once);
        }

        [Test]
        public void should_do_nothing_when_edition_is_blank()
        {
            Subject.Handle(new MovieFileAddedEvent(BuildMovieFile("")));

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.FindByMovieId(It.IsAny<int>()), Times.Never);

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Update(It.IsAny<MovieEditionSlot>()), Times.Never);

            Mocker.GetMock<IMovieEditionSlotRepository>()
                .Verify(s => s.Insert(It.IsAny<MovieEditionSlot>()), Times.Never);
        }
    }
}
