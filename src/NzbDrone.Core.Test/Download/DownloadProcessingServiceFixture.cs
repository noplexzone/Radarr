using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class DownloadProcessingServiceFixture : CoreTest<DownloadProcessingService>
    {
        [Test]
        public void should_group_pending_siblings_by_exact_client_and_ordinal_download_id()
        {
            var downloads = new List<TrackedDownload>
            {
                Build(1, "shared", MovieAcquisitionTarget.Main),
                Build(1, "shared", MovieAcquisitionTarget.ForEditionSlot(10)),
                Build(2, "shared", MovieAcquisitionTarget.Main),
                Build(2, "shared", MovieAcquisitionTarget.ForEditionSlot(20))
            };

            Mocker.GetMock<IConfigService>().SetupGet(s => s.EnableCompletedDownloadHandling).Returns(true);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(downloads);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.ImportPhysicalGroup(
                It.Is<IReadOnlyList<TrackedDownload>>(g => g.Count == 2 && g[0].DownloadClient == 1)), Times.Once());
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.ImportPhysicalGroup(
                It.Is<IReadOnlyList<TrackedDownload>>(g => g.Count == 2 && g[0].DownloadClient == 2)), Times.Once());
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(It.IsAny<TrackedDownload>()), Times.Never());
            Mocker.GetMock<IEventAggregator>().Verify(s => s.PublishEvent(It.IsAny<DownloadsProcessedEvent>()), Times.Once());
        }

        [Test]
        public void should_retain_imported_sibling_context_when_one_physical_sibling_is_pending()
        {
            var imported = Build(1, "shared", MovieAcquisitionTarget.Main);
            imported.State = TrackedDownloadState.Imported;
            imported.IsTrackable = false;
            var pending = Build(1, "shared", MovieAcquisitionTarget.ForEditionSlot(10));
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EnableCompletedDownloadHandling).Returns(true);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { imported, pending });

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.ImportPhysicalGroup(
                It.Is<IReadOnlyList<TrackedDownload>>(group => group.Count == 2 && group.Contains(imported) && group.Contains(pending))), Times.Once());
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(It.IsAny<TrackedDownload>()), Times.Never());
        }

        [Test]
        public void should_process_malformed_physical_identity_through_legacy_wrapper()
        {
            var malformed = Build(1, " ", MovieAcquisitionTarget.Main);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EnableCompletedDownloadHandling).Returns(true);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.GetTrackedDownloads()).Returns(new List<TrackedDownload> { malformed });

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.Import(malformed), Times.Once());
            Mocker.GetMock<ICompletedDownloadService>().Verify(s => s.ImportPhysicalGroup(It.IsAny<IReadOnlyList<TrackedDownload>>()), Times.Never());
        }

        private static TrackedDownload Build(int clientId, string downloadId, MovieAcquisitionTarget target)
        {
            return Builder<TrackedDownload>.CreateNew()
                .With(d => d.DownloadClient = clientId)
                .With(d => d.DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "Movie", CanBeRemoved = false })
                .With(d => d.RemoteMovie = new RemoteMovie { Movie = new Movie { Id = 1 }, AcquisitionTarget = target })
                .With(d => d.MovieId = 1)
                .With(d => d.AcquisitionTarget = target)
                .With(d => d.State = TrackedDownloadState.ImportPending)
                .With(d => d.IsTrackable = true)
                .Build();
        }
    }
}
