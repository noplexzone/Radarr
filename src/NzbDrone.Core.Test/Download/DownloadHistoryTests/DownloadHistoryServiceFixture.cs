using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadHistoryTests
{
    [TestFixture]
    public class DownloadHistoryServiceFixture : CoreTest<DownloadHistoryService>
    {
        private RemoteMovie BuildRemoteMovie(int? movieEditionSlotId = null)
        {
            return new RemoteMovie
            {
                Movie = new Movie { Id = 1 },
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.WEBDL1080p) },
                Release = new ReleaseInfo { Title = "Movie.2024.1080p", Indexer = "TestIndexer" },
                MovieEditionSlotId = movieEditionSlotId
            };
        }

        [Test]
        public void should_store_movie_edition_slot_id_in_download_history_when_present()
        {
            var remoteMovie = BuildRemoteMovie(movieEditionSlotId: 42);
            var grabbed = new MovieGrabbedEvent(remoteMovie)
            {
                DownloadId = "grab-1",
                DownloadClient = "qBittorrent",
                DownloadClientName = "qBittorrent"
            };

            Subject.Handle(grabbed);

            Mocker.GetMock<IDownloadHistoryRepository>()
                .Verify(r => r.Insert(It.Is<DownloadHistory>(h =>
                    h.EventType == DownloadHistoryEventType.DownloadGrabbed &&
                    h.Data.ContainsKey(MovieHistory.MOVIE_EDITION_SLOT_ID) &&
                    h.Data[MovieHistory.MOVIE_EDITION_SLOT_ID] == "42")));
        }

        [Test]
        public void should_not_store_movie_edition_slot_id_in_download_history_when_absent()
        {
            var remoteMovie = BuildRemoteMovie(movieEditionSlotId: null);
            var grabbed = new MovieGrabbedEvent(remoteMovie)
            {
                DownloadId = "grab-2",
                DownloadClient = "qBittorrent",
                DownloadClientName = "qBittorrent"
            };

            Subject.Handle(grabbed);

            Mocker.GetMock<IDownloadHistoryRepository>()
                .Verify(r => r.Insert(It.Is<DownloadHistory>(h =>
                    h.EventType == DownloadHistoryEventType.DownloadGrabbed &&
                    !h.Data.ContainsKey(MovieHistory.MOVIE_EDITION_SLOT_ID))));
        }

        [Test]
        public void should_skip_insert_when_download_id_is_empty()
        {
            var remoteMovie = BuildRemoteMovie(movieEditionSlotId: 5);
            var grabbed = new MovieGrabbedEvent(remoteMovie)
            {
                DownloadId = "",
                DownloadClient = "qBittorrent",
                DownloadClientName = "qBittorrent"
            };

            Subject.Handle(grabbed);

            Mocker.GetMock<IDownloadHistoryRepository>()
                .Verify(r => r.Insert(It.IsAny<DownloadHistory>()), Times.Never);
        }
    }
}
