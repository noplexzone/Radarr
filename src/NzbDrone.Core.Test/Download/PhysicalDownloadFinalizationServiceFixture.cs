using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class PhysicalDownloadFinalizationServiceFixture : CoreTest<PhysicalDownloadFinalizationService>
    {
        private Mock<IDownloadClient> _client;
        private DownloadClientDefinition _definition;

        [SetUp]
        public void SetUp()
        {
            _definition = new DownloadClientDefinition
            {
                Id = 7,
                Name = "Client",
                RemoveCompletedDownloads = true,
                RemoveFailedDownloads = true
            };

            _client = Mocker.GetMock<IDownloadClient>();
            _client.SetupGet(c => c.Definition).Returns(_definition);
            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(7)).Returns(_client.Object);
        }

        private static DownloadClientItem PhysicalItem(string downloadId = "shared")
        {
            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = "Shared download",
                Status = DownloadItemStatus.Completed,
                CanBeRemoved = true,
                DownloadClientInfo = new DownloadClientItemClientInfo { Id = 7, Name = "Client" }
            };
        }

        private static TrackedDownload LogicalDownload(TrackedDownloadState state, MovieAcquisitionTarget target = null, DownloadClientItem downloadItem = null)
        {
            return new TrackedDownload
            {
                DownloadClient = 7,
                DownloadItem = downloadItem ?? PhysicalItem(),
                MovieId = target?.EditionSlotId ?? 1,
                AcquisitionTarget = target ?? MovieAcquisitionTarget.Main,
                State = state,
                IsTrackable = true
            };
        }

        private void GivenSiblings(params TrackedDownload[] siblings)
        {
            Mocker.GetMock<ITrackedDownloadService>()
                .Setup(s => s.FindByDownloadClient(7, "shared"))
                .Returns(new List<TrackedDownload>(siblings));
        }

        [TestCase(TrackedDownloadState.Downloading)]
        [TestCase(TrackedDownloadState.ImportBlocked)]
        [TestCase(TrackedDownloadState.ImportPending)]
        [TestCase(TrackedDownloadState.Importing)]
        [TestCase(TrackedDownloadState.FailedPending)]
        public void imported_target_with_nonterminal_sibling_should_not_mutate_physical_item(TrackedDownloadState siblingState)
        {
            var item = PhysicalItem();
            var imported = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            var sibling = LogicalDownload(siblingState, MovieAcquisitionTarget.ForEditionSlot(42), item);
            GivenSiblings(imported, sibling);

            Subject.FinalizeTerminalDownload(imported);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Never());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void failed_target_with_active_sibling_should_not_remove_physical_item()
        {
            var item = PhysicalItem();
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            var active = LogicalDownload(TrackedDownloadState.Downloading, MovieAcquisitionTarget.ForEditionSlot(42), item);
            GivenSiblings(failed, active);

            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void last_of_three_grouped_imported_should_mark_once_and_retain_client_data()
        {
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            var slotA = LogicalDownload(TrackedDownloadState.Imported, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var slotB = LogicalDownload(TrackedDownloadState.Imported, MovieAcquisitionTarget.ForEditionSlot(43), item);
            GivenSiblings(main, slotA, slotB);

            Subject.FinalizeTerminalDownload(slotB);
            Subject.FinalizeTerminalDownload(slotB);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Once());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void grouped_all_failed_should_retain_client_data_even_when_configured()
        {
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            var slot = LogicalDownload(TrackedDownloadState.Failed, MovieAcquisitionTarget.ForEditionSlot(42), item);
            GivenSiblings(main, slot);

            Subject.FinalizeTerminalDownload(slot);
            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Never());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), true), Times.Never());
        }

        [Test]
        public void imported_group_should_not_remove_when_completed_removal_is_disabled()
        {
            _definition.RemoveCompletedDownloads = false;
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            var slot = LogicalDownload(TrackedDownloadState.Imported, MovieAcquisitionTarget.ForEditionSlot(42), item);
            GivenSiblings(main, slot);

            Subject.FinalizeTerminalDownload(slot);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Once());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void failed_group_should_not_remove_when_failed_removal_is_disabled()
        {
            _definition.RemoveFailedDownloads = false;
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            var slot = LogicalDownload(TrackedDownloadState.Failed, MovieAcquisitionTarget.ForEditionSlot(42), item);
            GivenSiblings(main, slot);

            Subject.FinalizeTerminalDownload(slot);

            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void mixed_terminal_states_should_retain_physical_item()
        {
            var item = PhysicalItem();
            var imported = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            var failed = LogicalDownload(TrackedDownloadState.Failed, MovieAcquisitionTarget.ForEditionSlot(42), item);
            var ignored = LogicalDownload(TrackedDownloadState.Ignored, MovieAcquisitionTarget.ForEditionSlot(43), item);
            GivenSiblings(imported, failed, ignored);

            Subject.FinalizeTerminalDownload(imported);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Never());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void unknown_target_should_block_finalization()
        {
            var item = PhysicalItem();
            var imported = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            var unknown = LogicalDownload(TrackedDownloadState.Imported, MovieAcquisitionTarget.Unknown, item);
            GivenSiblings(imported, unknown);

            Subject.FinalizeTerminalDownload(imported);

            _client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Never());
            _client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void existing_single_target_terminal_flow_should_remain()
        {
            var main = LogicalDownload(TrackedDownloadState.Imported);
            GivenSiblings(main);

            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(main.DownloadItem), Times.Once());
            _client.Verify(c => c.RemoveItem(main.DownloadItem, true), Times.Once());
            Assert.That(main.DownloadItem.Removed, Is.True);
        }

        [Test]
        public void refreshed_item_for_same_logical_lifecycle_should_not_repeat_mark()
        {
            _definition.RemoveCompletedDownloads = false;
            var firstItem = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: firstItem);
            GivenSiblings(main);

            Subject.FinalizeTerminalDownload(main);

            var refreshedItem = PhysicalItem();
            main.DownloadItem = refreshedItem;
            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(firstItem), Times.Once());
            _client.Verify(c => c.MarkItemAsImported(refreshedItem), Times.Never());
        }

        [Test]
        public void refreshed_item_for_same_logical_lifecycle_should_not_repeat_terminal_unsupported_remove()
        {
            var firstItem = PhysicalItem();
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: firstItem);
            GivenSiblings(failed);
            _client.Setup(c => c.RemoveItem(firstItem, true)).Throws<NotSupportedException>();

            Subject.FinalizeTerminalDownload(failed);

            var refreshedItem = PhysicalItem();
            failed.DownloadItem = refreshedItem;
            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(firstItem, true), Times.Once());
            _client.Verify(c => c.RemoveItem(refreshedItem, true), Times.Never());
        }

        [Test]
        public void reused_client_and_download_id_with_new_physical_item_should_finalize_new_lifecycle()
        {
            var firstItem = PhysicalItem();
            var first = LogicalDownload(TrackedDownloadState.Imported, downloadItem: firstItem);
            GivenSiblings(first);

            Subject.FinalizeTerminalDownload(first);

            var secondItem = PhysicalItem();
            var second = LogicalDownload(TrackedDownloadState.Imported, downloadItem: secondItem);
            GivenSiblings(second);

            Subject.FinalizeTerminalDownload(second);

            _client.Verify(c => c.MarkItemAsImported(firstItem), Times.Once());
            _client.Verify(c => c.MarkItemAsImported(secondItem), Times.Once());
            _client.Verify(c => c.RemoveItem(firstItem, true), Times.Once());
            _client.Verify(c => c.RemoveItem(secondItem, true), Times.Once());
        }

        [Test]
        public void transient_mark_failure_should_retry_and_only_record_success()
        {
            _definition.RemoveCompletedDownloads = false;
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            GivenSiblings(main);
            _client.SetupSequence(c => c.MarkItemAsImported(item))
                .Throws(new InvalidOperationException("transient"))
                .Pass();

            Subject.FinalizeTerminalDownload(main);
            Subject.FinalizeTerminalDownload(main);
            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(item), Times.Exactly(2));
        }

        [Test]
        public void transient_remove_failure_should_retry_and_only_record_success()
        {
            var item = PhysicalItem();
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            GivenSiblings(failed);
            _client.SetupSequence(c => c.RemoveItem(item, true))
                .Throws(new InvalidOperationException("transient"))
                .Pass();

            Subject.FinalizeTerminalDownload(failed);
            Subject.FinalizeTerminalDownload(failed);
            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(item, true), Times.Exactly(2));
            Assert.That(item.Removed, Is.True);
        }

        [Test]
        public void not_supported_mark_should_be_terminal_without_retry()
        {
            _definition.RemoveCompletedDownloads = false;
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            GivenSiblings(main);
            _client.Setup(c => c.MarkItemAsImported(item)).Throws<NotSupportedException>();

            Subject.FinalizeTerminalDownload(main);
            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(item), Times.Once());
        }

        [Test]
        public void not_supported_remove_should_be_terminal_without_retry()
        {
            var item = PhysicalItem();
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            GivenSiblings(failed);
            _client.Setup(c => c.RemoveItem(item, true)).Throws<NotSupportedException>();

            Subject.FinalizeTerminalDownload(failed);
            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(item, true), Times.Once());
            Assert.That(item.Removed, Is.False);
        }

        [Test]
        public void completed_downloading_item_should_mark_but_not_remove()
        {
            var item = PhysicalItem();
            item.Status = DownloadItemStatus.Downloading;
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            GivenSiblings(main);

            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(item), Times.Once());
            _client.Verify(c => c.RemoveItem(item, true), Times.Never());
        }

        [Test]
        public void failed_downloading_item_should_preserve_legacy_removal_behavior()
        {
            var item = PhysicalItem();
            item.Status = DownloadItemStatus.Downloading;
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            GivenSiblings(failed);

            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(item, true), Times.Once());
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void removal_should_respect_existing_removed_and_can_remove_flags(bool removed, bool canBeRemoved)
        {
            var item = PhysicalItem();
            item.Removed = removed;
            item.CanBeRemoved = canBeRemoved;
            var failed = LogicalDownload(TrackedDownloadState.Failed, downloadItem: item);
            GivenSiblings(failed);

            Subject.FinalizeTerminalDownload(failed);

            _client.Verify(c => c.RemoveItem(item, true), Times.Never());
        }

        [Test]
        public void completed_removal_should_wait_for_successful_mark_and_retry()
        {
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            GivenSiblings(main);
            _client.SetupSequence(c => c.MarkItemAsImported(item))
                .Throws(new InvalidOperationException("transient"))
                .Pass();

            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.RemoveItem(item, true), Times.Never());

            Subject.FinalizeTerminalDownload(main);

            _client.Verify(c => c.MarkItemAsImported(item), Times.Exactly(2));
            _client.Verify(c => c.RemoveItem(item, true), Times.Once());
        }

        [Test]
        public void queue_remove_failure_should_report_failure_and_remain_retryable()
        {
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Downloading, downloadItem: item);
            GivenSiblings(main);
            _client.SetupSequence(c => c.RemoveItem(item, true))
                .Throws(new InvalidOperationException("transient"))
                .Pass();

            Subject.ApplyQueueMutation(new[] { main }, false, true, false)
                .Should().Be(QueueMutationResult.DownloadClientMutationFailed);
            Subject.ApplyQueueMutation(new[] { main }, false, true, false)
                .Should().Be(QueueMutationResult.Applied);

            _client.Verify(c => c.RemoveItem(item, true), Times.Exactly(2));
            Assert.That(item.Removed, Is.True);
        }

        [Test]
        public void missing_client_or_definition_should_not_break_terminal_processing()
        {
            var item = PhysicalItem();
            var main = LogicalDownload(TrackedDownloadState.Imported, downloadItem: item);
            GivenSiblings(main);
            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(7)).Returns((IDownloadClient)null);

            Assert.DoesNotThrow(() => Subject.FinalizeTerminalDownload(main));

            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(7)).Returns(_client.Object);
            _client.SetupGet(c => c.Definition).Returns((DownloadClientDefinition)null);

            Assert.DoesNotThrow(() => Subject.FinalizeTerminalDownload(main));
            _client.Verify(c => c.MarkItemAsImported(item), Times.Once());
            _client.Verify(c => c.RemoveItem(item, true), Times.Never());
        }
    }
}
