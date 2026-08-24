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
using Radarr.Http.REST;

namespace NzbDrone.Api.Test.v3.Queue
{
    [TestFixture]
    public class QueueControllerFixture : TestBase<QueueController>
    {
        [SetUp]
        public void SetUp()
        {
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(new List<CustomFormat>());
            Mocker.SetConstant<IPhysicalDownloadFinalizationService>(Mocker.Resolve<PhysicalDownloadFinalizationService>());
        }

        private static DownloadClientItem PhysicalItem(int clientId, string downloadId)
        {
            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = "Shared download",
                CanBeRemoved = true,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = clientId, Name = "Client" }
            };
        }

        private static TrackedDownload LogicalDownload(int clientId, string downloadId, int movieId, MovieAcquisitionTarget target, DownloadClientItem downloadItem = null)
        {
            return new TrackedDownload
            {
                DownloadClient = clientId,
                DownloadItem = downloadItem ?? PhysicalItem(clientId, downloadId),
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
            var item = PhysicalItem(7, "shared");
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            var main = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.Main, item);
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
            var item = PhysicalItem(7, "shared");
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
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
            var item = PhysicalItem(7, "shared");
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(102)).Returns(QueueRow(102, slotB));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotB.Key)).Returns(slotB);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "shared")).Returns(new List<TrackedDownload> { slotA, slotB });
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
        public void single_exact_remove_with_active_sibling_should_not_remove_physical_item()
        {
            var item = PhysicalItem(7, "shared");
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            slotA.State = TrackedDownloadState.Failed;
            slotB.State = TrackedDownloadState.Downloading;
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "shared")).Returns(new List<TrackedDownload> { slotA, slotB });
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveAction(101, true, false, false, false);

            client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotA.Key), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotB.Key), Times.Never());
        }

        [Test]
        public void single_exact_remove_with_all_terminal_siblings_should_not_remove_physical_item()
        {
            var item = PhysicalItem(7, "terminal-shared");
            var slotA = LogicalDownload(7, "terminal-shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "terminal-shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            slotA.State = TrackedDownloadState.Failed;
            slotB.State = TrackedDownloadState.Failed;
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "terminal-shared")).Returns(new List<TrackedDownload> { slotA, slotB });
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveAction(101, true, false, false, false);

            client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotA.Key), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(slotB.Key), Times.Never());
        }

        [Test]
        public void bulk_subset_should_not_remove_physical_item()
        {
            var item = PhysicalItem(7, "shared");
            var main = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.Main, item);
            var slotA = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(100)).Returns(QueueRow(100, main));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "shared")).Returns(new List<TrackedDownload> { main, slotA, slotB });
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveMany(new QueueBulkResource { Ids = new List<int> { 100, 101 } }, true, false, false, false);

            client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.Is<List<TrackedDownloadKey>>(keys =>
                keys.Count == 2 && keys.Contains(main.Key) && keys.Contains(slotA.Key))), Times.Once());
        }

        [Test]
        public void bulk_terminal_subset_should_not_remove_physical_item()
        {
            var item = PhysicalItem(7, "terminal-shared");
            var main = LogicalDownload(7, "terminal-shared", 1, MovieAcquisitionTarget.Main, item);
            var slotA = LogicalDownload(7, "terminal-shared", 1, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(7, "terminal-shared", 1, MovieAcquisitionTarget.ForEditionSlot(43), item);
            main.State = TrackedDownloadState.Imported;
            slotA.State = TrackedDownloadState.Imported;
            slotB.State = TrackedDownloadState.Imported;
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(100)).Returns(QueueRow(100, main));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(101)).Returns(QueueRow(101, slotA));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(slotA.Key)).Returns(slotA);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "terminal-shared")).Returns(new List<TrackedDownload> { main, slotA, slotB });
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveMany(new QueueBulkResource { Ids = new List<int> { 100, 101 } }, true, false, false, false);

            client.Verify(s => s.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.Is<List<TrackedDownloadKey>>(keys =>
                keys.Count == 2 && keys.Contains(main.Key) && keys.Contains(slotA.Key))), Times.Once());
        }

        [Test]
        public void unavailable_physical_group_should_not_apply_logical_mutation_or_stop_tracking()
        {
            var main = LogicalDownload(7, "single", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, main));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "single")).Returns(new List<TrackedDownload>());

            Assert.Throws<BadRequestException>(() => Subject.RemoveAction(201, true, true, false, false));

            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(It.IsAny<TrackedDownload>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.IsAny<TrackedDownloadKey>()), Times.Never());
        }

        [Test]
        public void bulk_should_finalize_successful_group_before_later_group_failure()
        {
            var first = LogicalDownload(7, "first", 1, MovieAcquisitionTarget.Main);
            var second = LogicalDownload(8, "second", 2, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, first));
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(202)).Returns(QueueRow(202, second));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(first.Key)).Returns(first);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(second.Key)).Returns(second);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "first")).Returns(new List<TrackedDownload> { first });
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(8, "second")).Returns(new List<TrackedDownload> { second });
            var firstClient = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(firstClient.Object);
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(8)).Returns((IDownloadClient)null);

            Assert.Throws<BadRequestException>(() => Subject.RemoveMany(
                new QueueBulkResource { Ids = new List<int> { 201, 202 } }, true, true, false, false));

            firstClient.Verify(s => s.RemoveItem(first.DownloadItem, true), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(first, false), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(
                It.Is<List<TrackedDownloadKey>>(keys => keys.Count == 1 && keys.Contains(first.Key))), Times.Once());
            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(second, It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void failed_physical_queue_mutation_should_not_apply_logical_mutation_or_stop_tracking()
        {
            var main = LogicalDownload(7, "single", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, main));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "single")).Returns(new List<TrackedDownload> { main });
            var client = Mocker.GetMock<IDownloadClient>();
            client.Setup(s => s.RemoveItem(main.DownloadItem, true)).Throws(new System.InvalidOperationException("transient"));
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Assert.Throws<BadRequestException>(() => Subject.RemoveAction(201, true, true, false, false));

            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(It.IsAny<TrackedDownload>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.IsAny<TrackedDownloadKey>()), Times.Never());
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

        [Test]
        public void existing_single_target_remove_from_client_should_remain()
        {
            var main = LogicalDownload(7, "single", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, main));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "single")).Returns(new List<TrackedDownload> { main });
            var client = Mocker.GetMock<IDownloadClient>();
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns(client.Object);

            Subject.RemoveAction(201, true, false, false, false);

            client.Verify(s => s.RemoveItem(main.DownloadItem, true), Times.Once());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(main.Key), Times.Once());
        }

        [Test]
        public void missing_download_client_should_preserve_bad_request_and_not_apply_logical_actions()
        {
            var main = LogicalDownload(7, "single", 1, MovieAcquisitionTarget.Main);
            Mocker.GetMock<IQueueService>().Setup(s => s.Find(201)).Returns(QueueRow(201, main));
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.Find(main.Key)).Returns(main);
            Mocker.GetMock<ITrackedDownloadService>().Setup(s => s.FindByDownloadClient(7, "single")).Returns(new List<TrackedDownload> { main });
            Mocker.GetMock<IProvideDownloadClient>().Setup(s => s.Get(7)).Returns((IDownloadClient)null);

            Assert.Throws<BadRequestException>(() => Subject.RemoveAction(201, true, true, false, false));

            Mocker.GetMock<IFailedDownloadService>().Verify(s => s.MarkAsFailed(It.IsAny<TrackedDownload>(), It.IsAny<bool>()), Times.Never());
            Mocker.GetMock<ITrackedDownloadService>().Verify(s => s.StopTracking(It.IsAny<TrackedDownloadKey>()), Times.Never());
        }
    }
}
