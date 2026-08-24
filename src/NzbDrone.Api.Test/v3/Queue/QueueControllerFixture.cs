using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Queue;
using NzbDrone.Test.Common;
using Radarr.Api.V3.Queue;

namespace NzbDrone.Api.Test.v3.Queue
{
    [TestFixture]
    public class QueueControllerFixture : TestBase<QueueController>
    {
        [SetUp]
        public void SetUp()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
        }

        private static TrackedDownload LogicalDownload(int clientId, string downloadId, int movieId, MovieAcquisitionTarget target)
        {
            return new TrackedDownload
            {
                DownloadClient = clientId,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = $"Movie {target}",
                    DownloadClientInfo = new DownloadClientItemClientInfo { Id = clientId, Name = "Client" }
                },
                MovieId = movieId,
                AcquisitionTarget = target,
                IsTrackable = true
            };
        }

        private static NzbDrone.Core.Queue.Queue QueueRow(int id, TrackedDownload trackedDownload)
        {
            return new NzbDrone.Core.Queue.Queue
            {
                Id = id,
                DownloadId = trackedDownload.DownloadItem.DownloadId,
                AcquisitionTarget = trackedDownload.AcquisitionTarget,
                TrackedDownloadKey = trackedDownload.Key
            };
        }

        [Test]
        public void selecting_slot_a_should_resolve_and_ignore_only_slot_a()
        {
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43));
            var main = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<IIgnoredDownloadService>().Setup(s => s.IgnoreDownload(slotA)).Returns(true);

            Subject.RemoveAction(101, false, false, false, false);

            Mocker.GetMock<IIgnoredDownloadService>().Verify(s => s.IgnoreDownload(slotA), Times.Once());
            Mocker.GetMock<IIgnoredDownloadService>().Verify(s => s.IgnoreDownload(slotB), Times.Never());
            Mocker.GetMock<IIgnoredDownloadService>().Verify(s => s.IgnoreDownload(main), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotA.Key), Times.Once());
        }

        [Test]
        public void blocklisting_slot_a_should_publish_only_slot_a_failure_target()
        {
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);

            Subject.RemoveAction(101, false, true, false, false);

            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(slotA, false), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(slotB, It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotA.Key), Times.Once());
        }

        [Test]
        public void bulk_should_dedupe_logical_actions_by_exact_key_and_physical_removal_by_client_group()
        {
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42));
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(102)).Returns(QueueRow(102, slotB));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotB.Key)).Returns(slotB);
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveMany(new QueueBulkResource { Ids = new List<int> { 101, 101, 102 } }, true, true, false, false);

            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(slotA, false), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(slotB, false), Times.Once());
            client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.Is<List<TrackedDownloadKey>>(keys =>
                keys.Count == 2 && keys.Contains(slotA.Key) && keys.Contains(slotB.Key))), Times.Once());
        }

        [Test]
        public void existing_single_target_queue_behavior_should_remain()
        {
            var main = LogicalDownload(7, "single", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, main));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<IIgnoredDownloadService>().Setup(s => s.IgnoreDownload(main)).Returns(true);

            Subject.RemoveAction(201, false, false, false, false);

            Mocker.GetMock<IIgnoredDownloadService>().Verify(s => s.IgnoreDownload(main), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(main.Key), Times.Once());
        }
    }
}
