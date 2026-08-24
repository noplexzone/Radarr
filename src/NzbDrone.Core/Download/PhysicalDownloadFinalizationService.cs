using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Download
{
    public interface IPhysicalDownloadFinalizationService
    {
        void FinalizeTerminalDownload(TrackedDownload trackedDownload);
        QueueMutationResult ApplyQueueMutation(IReadOnlyCollection<TrackedDownload> selectedDownloads, bool isBulk, bool removeFromClient, bool changeCategory);
    }

    public enum QueueMutationResult
    {
        Skipped,
        Applied,
        DownloadClientUnavailable,
        PhysicalGroupUnavailable,
        DownloadClientMutationFailed
    }

    public class PhysicalDownloadFinalizationService : IPhysicalDownloadFinalizationService
    {
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly Logger _logger;
        private readonly object _mutationLock = new ();

        public PhysicalDownloadFinalizationService(ITrackedDownloadService trackedDownloadService,
                                                   IProvideDownloadClient downloadClientProvider,
                                                   Logger logger)
        {
            _trackedDownloadService = trackedDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _logger = logger;
        }

        public void FinalizeTerminalDownload(TrackedDownload trackedDownload)
        {
            if (!TryGetExactSiblings(trackedDownload, out var siblings))
            {
                return;
            }

            var disposition = GetTerminalDisposition(siblings);

            if (disposition == TerminalDisposition.Retain)
            {
                return;
            }

            var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

            if (downloadClient == null)
            {
                return;
            }

            var definition = downloadClient.Definition as DownloadClientDefinition;

            lock (_mutationLock)
            {
                if (disposition == TerminalDisposition.Imported)
                {
                    var markResult = MarkItemAsImportedOnce(trackedDownload, siblings, downloadClient);

                    if (markResult != PhysicalMutationResult.Failed &&
                        siblings.Count == 1 &&
                        definition?.RemoveCompletedDownloads == true &&
                        CanRemoveCompleted(trackedDownload))
                    {
                        RemoveItemOnce(trackedDownload, siblings, downloadClient);
                    }
                }
                else if (siblings.Count == 1 && definition?.RemoveFailedDownloads == true && CanRemoveFailed(trackedDownload))
                {
                    RemoveItemOnce(trackedDownload, siblings, downloadClient);
                }
            }
        }

        public QueueMutationResult ApplyQueueMutation(IReadOnlyCollection<TrackedDownload> selectedDownloads, bool isBulk, bool removeFromClient, bool changeCategory)
        {
            if ((!removeFromClient && !changeCategory) || selectedDownloads == null || selectedDownloads.Count == 0)
            {
                return QueueMutationResult.Skipped;
            }

            var selected = selectedDownloads.DistinctBy(download => download.Key).ToList();
            var trackedDownload = selected[0];

            if (!TryGetExactSiblings(trackedDownload, out var siblings) ||
                selected.Any(download => download.DownloadClient != trackedDownload.DownloadClient ||
                    !download.DownloadItem.DownloadId.Equals(trackedDownload.DownloadItem.DownloadId, StringComparison.Ordinal)))
            {
                return QueueMutationResult.PhysicalGroupUnavailable;
            }

            var siblingKeys = siblings.Select(download => download.Key).ToHashSet();
            var selectedKeys = selected.Select(download => download.Key).ToHashSet();
            var selectedEverySibling = selectedKeys.SetEquals(siblingKeys);
            var singleTargetPhysicalDownload = siblings.Count == 1 && selectedEverySibling;
            var disposition = GetTerminalDisposition(siblings);
            var bulkRemoveOverride = isBulk && removeFromClient && selectedEverySibling;
            var bulkCategoryOverride = isBulk && changeCategory && selectedEverySibling && disposition == TerminalDisposition.Imported;

            if (!singleTargetPhysicalDownload && !bulkRemoveOverride && !bulkCategoryOverride)
            {
                return QueueMutationResult.Skipped;
            }

            var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

            if (downloadClient == null)
            {
                return QueueMutationResult.DownloadClientUnavailable;
            }

            PhysicalMutationResult mutationResult;

            lock (_mutationLock)
            {
                if (removeFromClient && (singleTargetPhysicalDownload || bulkRemoveOverride))
                {
                    mutationResult = RemoveItemOnce(trackedDownload, siblings, downloadClient);
                }
                else if (changeCategory &&
                         (singleTargetPhysicalDownload || bulkCategoryOverride))
                {
                    mutationResult = MarkItemAsImportedOnce(trackedDownload, siblings, downloadClient);
                }
                else
                {
                    return QueueMutationResult.Skipped;
                }
            }

            return mutationResult == PhysicalMutationResult.Failed
                ? QueueMutationResult.DownloadClientMutationFailed
                : QueueMutationResult.Applied;
        }

        private bool TryGetExactSiblings(TrackedDownload trackedDownload, out List<TrackedDownload> siblings)
        {
            siblings = null;

            if (trackedDownload?.DownloadItem == null || string.IsNullOrWhiteSpace(trackedDownload.DownloadItem.DownloadId))
            {
                return false;
            }

            siblings = _trackedDownloadService.FindByDownloadClient(trackedDownload.DownloadClient, trackedDownload.DownloadItem.DownloadId)?
                .DistinctBy(download => download.Key)
                .ToList();

            return siblings is { Count: > 0 } && siblings.Any(download => download.Key == trackedDownload.Key);
        }

        private static TerminalDisposition GetTerminalDisposition(List<TrackedDownload> siblings)
        {
            if (siblings.Any(download => download.AcquisitionTarget == null || download.AcquisitionTarget.Equals(NzbDrone.Core.Movies.MovieAcquisitionTarget.Unknown)))
            {
                return TerminalDisposition.Retain;
            }

            if (siblings.All(download => download.State == TrackedDownloadState.Imported))
            {
                return TerminalDisposition.Imported;
            }

            if (siblings.All(download => download.State == TrackedDownloadState.Failed))
            {
                return TerminalDisposition.Failed;
            }

            return TerminalDisposition.Retain;
        }

        private static bool CanRemoveFailed(TrackedDownload trackedDownload)
        {
            return !trackedDownload.DownloadItem.Removed &&
                   trackedDownload.DownloadItem.CanBeRemoved;
        }

        private static bool CanRemoveCompleted(TrackedDownload trackedDownload)
        {
            return CanRemoveFailed(trackedDownload) &&
                   trackedDownload.DownloadItem.Status != DownloadItemStatus.Downloading;
        }

        private PhysicalMutationResult MarkItemAsImportedOnce(TrackedDownload trackedDownload, List<TrackedDownload> siblings, IDownloadClient downloadClient)
        {
            if (siblings.All(download => download.PhysicalItemMarkedAsImported) || trackedDownload.DownloadItem.Removed)
            {
                return PhysicalMutationResult.AlreadyApplied;
            }

            try
            {
                _logger.Debug("[{0}] Marking download as imported from {1}", trackedDownload.DownloadItem.Title, trackedDownload.DownloadItem.DownloadClientInfo.Name);
                downloadClient.MarkItemAsImported(trackedDownload.DownloadItem);
                siblings.ForEach(download => download.PhysicalItemMarkedAsImported = true);
                return PhysicalMutationResult.Applied;
            }
            catch (NotSupportedException e)
            {
                siblings.ForEach(download => download.PhysicalItemMarkedAsImported = true);
                _logger.Debug(e.Message);
                return PhysicalMutationResult.NotSupported;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't mark item {0} as imported from client {1}", trackedDownload.DownloadItem.Title, downloadClient.Name);
                return PhysicalMutationResult.Failed;
            }
        }

        private PhysicalMutationResult RemoveItemOnce(TrackedDownload trackedDownload, List<TrackedDownload> siblings, IDownloadClient downloadClient)
        {
            if (siblings.All(download => download.PhysicalItemRemovalFinalized) || trackedDownload.DownloadItem.Removed)
            {
                return PhysicalMutationResult.AlreadyApplied;
            }

            try
            {
                _logger.Debug("[{0}] Removing download from {1} history", trackedDownload.DownloadItem.Title, trackedDownload.DownloadItem.DownloadClientInfo.Name);
                downloadClient.RemoveItem(trackedDownload.DownloadItem, true);
                siblings.ForEach(download => download.PhysicalItemRemovalFinalized = true);
                trackedDownload.DownloadItem.Removed = true;
                return PhysicalMutationResult.Applied;
            }
            catch (NotSupportedException)
            {
                siblings.ForEach(download => download.PhysicalItemRemovalFinalized = true);
                _logger.Warn("Removing item not supported by your download client ({0}).", downloadClient.Definition.Name);
                return PhysicalMutationResult.NotSupported;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't remove item {0} from client {1}", trackedDownload.DownloadItem.Title, downloadClient.Name);
                return PhysicalMutationResult.Failed;
            }
        }

        private enum PhysicalMutationResult
        {
            Applied,
            AlreadyApplied,
            NotSupported,
            Failed
        }

        private enum TerminalDisposition
        {
            Retain,
            Imported,
            Failed
        }
    }
}
