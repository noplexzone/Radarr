using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class DownloadMonitoringServiceFixture : CoreTest<DownloadMonitoringService>
    {
        [Test]
        public void terminal_download_returned_by_client_should_remain_physically_present()
        {
            var definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "Client",
                Protocol = DownloadProtocol.Torrent
            };
            var item = new DownloadClientItem
            {
                DownloadId = "shared",
                Title = "Movie.2024.1080p",
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Client" }
            };
            var tracked = new TrackedDownload
            {
                DownloadClient = 7,
                DownloadItem = item,
                MovieId = 1,
                AcquisitionTarget = MovieAcquisitionTarget.Main,
                State = TrackedDownloadState.Imported,
                IsTrackable = true,
                PhysicalItemMarkedAsImported = true
            };
            var client = Mocker.GetMock<IDownloadClient>();
            client.SetupGet(c => c.Definition).Returns(definition);
            client.Setup(c => c.GetItems()).Returns(new[] { item });
            Mocker.GetMock<IDownloadClientFactory>()
                .Setup(f => f.DownloadHandlingEnabled(true))
                .Returns(new List<IDownloadClient> { client.Object });
            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(s => s.TrackDownload(definition, item))
                .Returns(new List<TrackedDownload> { tracked });

            Subject.Execute(new RefreshMonitoredDownloadsCommand());

            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.UpdateTrackable(
                It.Is<List<TrackedDownload>>(downloads => downloads.Count == 1 && downloads[0] == tracked)), Times.Once());
        }

        [Test]
        public void failed_client_poll_should_preserve_cached_physical_presence()
        {
            var definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "Offline Client",
                Protocol = DownloadProtocol.Torrent
            };
            var item = new DownloadClientItem
            {
                DownloadId = "shared",
                Title = "Movie.2024.1080p",
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Offline Client" }
            };
            var tracked = new TrackedDownload
            {
                DownloadClient = 7,
                DownloadItem = item,
                MovieId = 1,
                AcquisitionTarget = MovieAcquisitionTarget.Main,
                State = TrackedDownloadState.Imported,
                IsTrackable = true,
                PhysicalItemMarkedAsImported = true,
                PhysicalItemRemovalFinalized = true
            };
            var client = Mocker.GetMock<IDownloadClient>();
            client.SetupGet(c => c.Definition).Returns(definition);
            client.Setup(c => c.GetItems()).Throws(new System.InvalidOperationException("offline"));
            Mocker.GetMock<IDownloadClientFactory>()
                .Setup(f => f.DownloadHandlingEnabled(true))
                .Returns(new List<IDownloadClient> { client.Object });
            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(s => s.GetTrackedDownloads())
                .Returns(new List<TrackedDownload> { tracked });

            Subject.Execute(new RefreshMonitoredDownloadsCommand());

            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.UpdateTrackable(
                It.Is<List<TrackedDownload>>(downloads => downloads.Count == 1 && downloads[0] == tracked)), Times.Once());
        }
    }
}
