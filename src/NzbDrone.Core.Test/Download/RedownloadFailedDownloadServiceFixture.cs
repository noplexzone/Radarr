using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
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
            Subject.Handle(new DownloadFailedEvent { MovieId = 1 });
            Mocker.GetMock<IManageCommandQueue>().Verify(q => q.Push(It.Is<MoviesSearchCommand>(c => c.MovieIds[0] == 1), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), Times.Once());
        }
    }
}
