using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        void ImportPhysicalGroup(IReadOnlyList<TrackedDownload> trackedDownloads);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IDownloadedMovieImportService _downloadedMovieImportService;
        private readonly IParsingService _parsingService;
        private readonly IMovieService _movieService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IRejectedImportService _rejectedImportService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IDownloadHistoryService downloadHistoryService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedMovieImportService downloadedMovieImportService,
                                        IParsingService parsingService,
                                        IMovieService movieService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IRejectedImportService rejectedImportService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _provideImportItemService = provideImportItemService;
            _downloadedMovieImportService = downloadedMovieImportService;
            _parsingService = parsingService;
            _movieService = movieService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _rejectedImportService = rejectedImportService;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == MovieHistoryEventType.Grabbed).ToList();
            var historyItem = grabbedHistories.MaxBy(h => h.Date);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Radarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            var movie = _parsingService.GetMovie(trackedDownload.DownloadItem.Title);

            if (movie == null)
            {
                if (historyItem != null)
                {
                    movie = _movieService.GetMovie(historyItem.MovieId);
                }

                if (movie == null)
                {
                    trackedDownload.Warn("Movie title mismatch, automatic import is not possible. Manual Import required.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }

                Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.MOVIE_MATCH_TYPE, MovieMatchType.Unknown.ToString()), out MovieMatchType movieMatchType);
                Enum.TryParse(historyItem.Data.GetValueOrDefault(MovieHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

                // Show a warning if the release was matched by ID and the source is not interactive search
                if (movieMatchType == MovieMatchType.Id && releaseSource != ReleaseSourceType.InteractiveSearch)
                {
                    trackedDownload.Warn("Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            if (trackedDownload.RemoteMovie?.Movie == null)
            {
                trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var importResults = _downloadedMovieImportService.ProcessPath(outputPath,
                ImportMode.Auto,
                trackedDownload.RemoteMovie.Movie,
                trackedDownload.ImportItem);

            HandleImportResults(trackedDownload, outputPath, importResults, false);
        }

        public void ImportPhysicalGroup(IReadOnlyList<TrackedDownload> trackedDownloads)
        {
            if (trackedDownloads == null || trackedDownloads.Count < 2)
            {
                foreach (var trackedDownload in trackedDownloads ?? Array.Empty<TrackedDownload>())
                {
                    Import(trackedDownload);
                }

                return;
            }

            var pendingDownloads = trackedDownloads.Where(download => download.State == TrackedDownloadState.ImportPending).ToList();
            if (pendingDownloads.Count == 0)
            {
                return;
            }

            foreach (var trackedDownload in trackedDownloads)
            {
                SetImportItem(trackedDownload);
            }

            var first = trackedDownloads[0];
            var outputPath = first.ImportItem?.OutputPath.FullPath;
            var valid = first.Key.IsValid &&
                        first.AcquisitionTarget.Kind != MovieAcquisitionTargetKind.Unknown &&
                        trackedDownloads.Select(download => download.Key).Distinct().Count() == trackedDownloads.Count &&
                        trackedDownloads.All(download =>
                            download.Key.IsValid &&
                            download.AcquisitionTarget.Kind != MovieAcquisitionTargetKind.Unknown &&
                            download.DownloadClient == first.DownloadClient &&
                            download.DownloadItem.DownloadId.Equals(first.DownloadItem.DownloadId, StringComparison.Ordinal) &&
                            download.ImportItem?.OutputPath.FullPath.Equals(outputPath, StringComparison.Ordinal) == true &&
                            download.RemoteMovie?.Movie?.Id == download.Key.MovieId &&
                            download.RemoteMovie.AcquisitionTarget.Equals(download.Key.AcquisitionTarget) &&
                            ValidatePath(download));

            if (!valid)
            {
                foreach (var trackedDownload in pendingDownloads)
                {
                    trackedDownload.Warn("Grouped import context did not match the exact physical download and target.");
                    SetStateToImportBlocked(trackedDownload, true);
                }

                return;
            }

            foreach (var trackedDownload in pendingDownloads)
            {
                trackedDownload.State = TrackedDownloadState.Importing;
            }

            var envelopes = trackedDownloads
                .Select(download => new PhysicalDownloadImportEnvelope(download.Key, download.RemoteMovie, download.ImportItem, pendingDownloads.Contains(download)))
                .ToList();
            try
            {
                var groupedResults = _downloadedMovieImportService.ProcessPhysicalGroup(outputPath, envelopes);
                foreach (var trackedDownload in pendingDownloads)
                {
                    var result = groupedResults.SingleOrDefault(item => item.Key == trackedDownload.Key);
                    HandleImportResults(trackedDownload, outputPath, result?.ImportResults ?? new List<ImportResult>(), true);
                }
            }
            catch
            {
                foreach (var trackedDownload in trackedDownloads.Where(download => download.State == TrackedDownloadState.Importing))
                {
                    trackedDownload.State = TrackedDownloadState.ImportBlocked;
                }

                throw;
            }
        }

        private void HandleImportResults(TrackedDownload trackedDownload, string outputPath, List<ImportResult> importResults, bool requireExactTarget)
        {
            if (VerifyImport(trackedDownload, importResults, requireExactTarget))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                if (requireExactTarget)
                {
                    SetStateToImportBlocked(trackedDownload, true);
                }

                return;
            }

            if (importResults.Count == 1 && _rejectedImportService.Process(trackedDownload, importResults.First()))
            {
                if (requireExactTarget && trackedDownload.State == TrackedDownloadState.ImportPending)
                {
                    SetStateToImportBlocked(trackedDownload, true);
                }

                return;
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
            {
                new TrackedDownloadStatusMessage("One or more movies expected in this release were not imported or missing", new List<string>())
            };

            statusMessages.AddRange(importResults
                .Where(result => result.Result != ImportResultType.Imported && result.ImportDecision.LocalMovie != null)
                .OrderBy(result => result.ImportDecision.LocalMovie.Path)
                .Select(result => new TrackedDownloadStatusMessage(Path.GetFileName(result.ImportDecision.LocalMovie.Path), result.Errors)));

            trackedDownload.Warn(statusMessages.ToArray());
            SetStateToImportBlocked(trackedDownload, requireExactTarget);
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            return VerifyImport(trackedDownload, importResults, false);
        }

        private bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults, bool requireExactTarget)
        {
            var allMoviesImported = importResults.Any(result =>
                result.Result == ImportResultType.Imported &&
                (!requireExactTarget ||
                 (result.ImportDecision.LocalMovie?.Movie?.Id == trackedDownload.Key.MovieId &&
                  result.ImportDecision.LocalMovie.AcquisitionTarget.Equals(trackedDownload.Key.AcquisitionTarget))));

            if (allMoviesImported)
            {
                _logger.Debug("All movies were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));
                return true;
            }

            // Double check if all movies were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.
            var atLeastOneMovieImported = importResults.Any(c => c.Result == ImportResultType.Imported);

            bool allMoviesImportedInHistory;
            if (requireExactTarget)
            {
                var latestLifecycle = _downloadHistoryService.GetLatestDownloadHistoryItemForTarget(
                    trackedDownload.Key.DownloadId,
                    trackedDownload.Key.DownloadClientId,
                    trackedDownload.Key.MovieId,
                    trackedDownload.Key.AcquisitionTarget);
                allMoviesImportedInHistory = latestLifecycle?.EventType == DownloadHistoryEventType.DownloadImported ||
                                             latestLifecycle?.EventType == DownloadHistoryEventType.FileImported;
            }
            else
            {
                var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();
                allMoviesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);
            }

            if (allMoviesImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.
                if (atLeastOneMovieImported)
                {
                    _logger.Debug("All movies were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No Movies were just imported, but all movies were previously imported, possible issue with download history.")
                           .Property("MovieId", trackedDownload.RemoteMovie.Movie.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.ImportItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteMovie.Movie.Id));

                return true;
            }

            _logger.Debug("Not all movies have been imported for {0}", trackedDownload.DownloadItem.Title);
            return false;
        }

        private void SetStateToImportBlocked(TrackedDownload trackedDownload, bool requireExactTarget = false)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;

            if (!trackedDownload.HasNotifiedManualInteractionRequired)
            {
                GrabbedReleaseInfo releaseInfo;
                if (requireExactTarget)
                {
                    var grabbedHistory = _downloadHistoryService.GetLatestGrabForTarget(
                        trackedDownload.Key.DownloadId,
                        trackedDownload.Key.DownloadClientId,
                        trackedDownload.Key.MovieId,
                        trackedDownload.Key.AcquisitionTarget);
                    releaseInfo = grabbedHistory == null ? null : new GrabbedReleaseInfo(grabbedHistory);
                }
                else
                {
                    var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                        .Where(h => h.EventType == MovieHistoryEventType.Grabbed)
                        .ToList();
                    releaseInfo = grabbedHistories.Count > 0 ? new GrabbedReleaseInfo(grabbedHistories) : null;
                }

                trackedDownload.HasNotifiedManualInteractionRequired = true;

                var manualInteractionEvent = new ManualInteractionRequiredEvent(trackedDownload, releaseInfo);

                _eventAggregator.PublishEvent(manualInteractionEvent);
            }
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
