using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        private void ProcessImportGroup(List<TrackedDownload> downloads)
        {
            try
            {
                if (downloads.Count > 1)
                {
                    _completedDownloadService.ImportPhysicalGroup(downloads);
                }
                else
                {
                    _completedDownloadService.Import(downloads[0]);
                }
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to process download: {0}", downloads[0].DownloadItem.Title);
            }
        }

        private static bool HasKnownPhysicalIdentity(TrackedDownload download)
        {
            return download?.Key.IsValid == true &&
                   download.AcquisitionTarget.Kind != NzbDrone.Core.Movies.MovieAcquisitionTargetKind.Unknown;
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var allTrackedDownloads = _trackedDownloadService.GetTrackedDownloads().ToList();
            var trackedDownloads = allTrackedDownloads.Where(t => t.IsTrackable).ToList();

            if (enableCompletedDownloadHandling)
            {
                var pending = trackedDownloads.Where(download => download.State == TrackedDownloadState.ImportPending).ToList();
                var pendingGroups = pending
                    .Where(HasKnownPhysicalIdentity)
                    .GroupBy(download => (download.DownloadClient, download.DownloadItem.DownloadId))
                    .ToList();
                var groupedPending = pendingGroups.SelectMany(group => group).ToHashSet();

                foreach (var pendingGroup in pendingGroups)
                {
                    var physicalSiblings = allTrackedDownloads
                        .Where(HasKnownPhysicalIdentity)
                        .Where(download => download.DownloadClient == pendingGroup.Key.DownloadClient &&
                                           download.DownloadItem.DownloadId.Equals(pendingGroup.Key.DownloadId, StringComparison.Ordinal))
                        .ToList();
                    ProcessImportGroup(physicalSiblings);
                }

                // Malformed/unknown identities deliberately retain the legacy one-item path.
                foreach (var trackedDownload in pending.Where(download => !groupedPending.Contains(download)))
                {
                    ProcessImportGroup(new List<TrackedDownload> { trackedDownload });
                }
            }

            // Process failures after imports so a failed import can be handled in this execution.
            foreach (var trackedDownload in trackedDownloads.Where(download => download.State == TrackedDownloadState.FailedPending))
            {
                try
                {
                    _failedDownloadService.ProcessFailed(trackedDownload);
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
