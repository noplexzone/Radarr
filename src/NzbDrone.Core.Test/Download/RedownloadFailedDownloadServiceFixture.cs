using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Test.Framework;
namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class RedownloadFailedDownloadServiceFixture : CoreTest<RedownloadFailedDownloadService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.AutoRedownloadFailed).Returns(true);
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetForMovie(1)).Returns(new List<MovieEditionSlot> { new MovieEditionSlot { Id = 42, MovieId = 1 } });
        }
        [Test]
        public void should_search_exact_edition_after_slot_failure()
        {
            Subject.Handle(new DownloadFailedEvent { MovieId = 1, MovieEditionSlotId = 42 });
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<MovieEditionSearchCommand>(c => c.MovieId == 1 && c.MovieEditionSlotId == 42), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MoviesSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }
        [Test]
        public void should_search_movie_after_main_failure()
        {
            Subject.Handle(new DownloadFailedEvent { MovieId = 1, AcquisitionTarget = MovieAcquisitionTarget.Main });
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<MoviesSearchCommand>(c => c.MovieIds[0] == 1), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());
        }

        [Test]
        public void should_fail_closed_without_searching_when_target_is_unknown()
        {
            Subject.Handle(new DownloadFailedEvent { MovieId = 1, AcquisitionTarget = MovieAcquisitionTarget.Unknown });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MoviesSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MovieEditionSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }

        [Test]
        public void conflicting_main_and_slot_history_should_never_trigger_main_redownload()
        {
            var data = new Dictionary<string, string>
            {
                [NzbDrone.Core.History.MovieHistory.ACQUISITION_TARGET] = "main",
                [NzbDrone.Core.History.MovieHistory.MOVIE_EDITION_SLOT_ID] = "42"
            };

            Subject.Handle(new DownloadFailedEvent
            {
                MovieId = 1,
                AcquisitionTarget = MovieAcquisitionTargetSerializer.ReadLegacyHistory(data)
            });

            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MoviesSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.IsAny<MovieEditionSearchCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Never());
        }
    }
}
