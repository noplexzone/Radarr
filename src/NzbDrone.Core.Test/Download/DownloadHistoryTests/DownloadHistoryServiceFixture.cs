using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
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
        public void should_select_latest_lifecycle_and_grab_for_exact_nullable_target()
        {
            var mainGrab = new DownloadHistory { EventType = DownloadHistoryEventType.DownloadGrabbed };
            var slotGrab = new DownloadHistory { EventType = DownloadHistoryEventType.DownloadGrabbed };
            slotGrab.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "42");
            var otherFailure = new DownloadHistory { EventType = DownloadHistoryEventType.DownloadFailed };
            otherFailure.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "43");
            Mocker.GetMock<IDownloadHistoryRepository>().Setup(r => r.FindByDownloadId("shared")).Returns(new List<DownloadHistory> { otherFailure, slotGrab, mainGrab });

            Subject.GetLatestDownloadHistoryItem("shared", 42).Should().BeSameAs(slotGrab);
            Subject.GetLatestGrab("shared", 42).Should().BeSameAs(slotGrab);
            Subject.GetLatestDownloadHistoryItem("shared", null).Should().BeSameAs(mainGrab);
            Subject.GetLatestGrab("shared", null).Should().BeSameAs(mainGrab);
        }

        [Test]
        public void malformed_slot_identity_should_not_match_main_download_history()
        {
            var malformed = new DownloadHistory { EventType = DownloadHistoryEventType.DownloadFailed };
            malformed.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "not-an-id");
            Mocker.GetMock<IDownloadHistoryRepository>().Setup(r => r.FindByDownloadId("corrupt")).Returns(new List<DownloadHistory> { malformed });

            Subject.GetLatestDownloadHistoryItem("corrupt", null).Should().BeNull();
        }

        [Test]
        public void should_filter_lifecycle_and_grab_by_exact_logical_identity()
        {
            var target = MovieAcquisitionTarget.ForEditionSlot(42);
            var exactGrab = History(DownloadHistoryEventType.DownloadGrabbed, 1, 7, target);
            var exactFailure = History(DownloadHistoryEventType.DownloadFailed, 1, 7, target);
            var wrongClient = History(DownloadHistoryEventType.DownloadFailed, 1, 8, target);
            var wrongMovie = History(DownloadHistoryEventType.DownloadFailed, 2, 7, target);
            var wrongTarget = History(DownloadHistoryEventType.DownloadFailed, 1, 7, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IDownloadHistoryRepository>()
                .Setup(r => r.FindByDownloadId("reused"))
                .Returns(new List<DownloadHistory> { wrongClient, wrongMovie, wrongTarget, exactFailure, exactGrab });

            Subject.GetLatestDownloadHistoryItemForTarget("reused", 7, 1, target).Should().BeSameAs(exactFailure);
            Subject.GetLatestGrabForTarget("reused", 7, 1, target).Should().BeSameAs(exactGrab);
            Subject.GetGrabs("reused", 7).Should().Equal(exactGrab);
        }

        private static DownloadHistory History(DownloadHistoryEventType eventType, int movieId, int downloadClientId, MovieAcquisitionTarget target)
        {
            var history = new DownloadHistory
            {
                EventType = eventType,
                MovieId = movieId,
                DownloadClientId = downloadClientId,
                DownloadId = "reused"
            };
            MovieAcquisitionTargetSerializer.Write(history.Data, target);
            return history;
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
