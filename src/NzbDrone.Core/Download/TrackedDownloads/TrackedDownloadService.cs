using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public interface ITrackedDownloadService
    {
        TrackedDownload Find(string downloadId);
        void StopTracking(string downloadId);
        void StopTracking(List<string> downloadIds);
        TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
        List<TrackedDownload> GetTrackedDownloads();
        void UpdateTrackable(List<TrackedDownload> trackedDownloads);
    }

    public class TrackedDownloadService : ITrackedDownloadService,
                                          IHandle<MovieAddedEvent>,
                                          IHandle<MovieEditedEvent>,
                                          IHandle<MoviesBulkEditedEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IParsingService _parsingService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IConfigService _config;
        private readonly IRemoteMovieAggregationService _aggregationService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IMovieEditionSlotService _movieEditionSlotService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;
        private readonly ICached<TrackedDownload> _cache;

        public TrackedDownloadService(IParsingService parsingService,
                                      ICacheManager cacheManager,
                                      IHistoryService historyService,
                                      IConfigService config,
                                      IRemoteMovieAggregationService aggregationService,
                                      ICustomFormatCalculationService formatCalculator,
                                      IEventAggregator eventAggregator,
                                      IDownloadHistoryService downloadHistoryService,
                                      IMovieEditionSlotService movieEditionSlotService,
                                      IQualityProfileService qualityProfileService,
                                      IMediaFileService mediaFileService,
                                      Logger logger)
        {
            _parsingService = parsingService;
            _historyService = historyService;
            _cache = cacheManager.GetCache<TrackedDownload>(GetType());
            _config = config;
            _aggregationService = aggregationService;
            _formatCalculator = formatCalculator;
            _eventAggregator = eventAggregator;
            _downloadHistoryService = downloadHistoryService;
            _movieEditionSlotService = movieEditionSlotService;
            _qualityProfileService = qualityProfileService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public TrackedDownload Find(string downloadId)
        {
            return _cache.Find(downloadId);
        }

        public void StopTracking(string downloadId)
        {
            var trackedDownload = _cache.Find(downloadId);

            _cache.Remove(downloadId);
            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(new List<TrackedDownload> { trackedDownload }));
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = new List<TrackedDownload>();

            foreach (var downloadId in downloadIds)
            {
                var trackedDownload = _cache.Find(downloadId);

                _cache.Remove(downloadId);
                trackedDownloads.Add(trackedDownload);
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public TrackedDownload TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var existingItem = Find(downloadItem.DownloadId);

            if (existingItem != null && existingItem.State != TrackedDownloadState.Downloading)
            {
                LogItemChange(existingItem, existingItem.DownloadItem, downloadItem);

                existingItem.DownloadItem = downloadItem;
                existingItem.IsTrackable = true;

                return existingItem;
            }

            var trackedDownload = new TrackedDownload
            {
                DownloadClient = downloadClient.Id,
                DownloadItem = downloadItem,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false
            };

            try
            {
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(h => h.Date)
                    .ToList();
                var latestMovieGrab = historyItems.FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed);
                var latestDownloadGrab = _downloadHistoryService.GetLatestGrab(downloadItem.DownloadId);
                var movieGrabTarget = ReadHistoryTarget(latestMovieGrab?.Data);
                var downloadGrabTarget = ReadHistoryTarget(latestDownloadGrab?.Data);
                var target = ResolveReconstructionTarget(latestMovieGrab, movieGrabTarget, latestDownloadGrab, downloadGrabTarget);
                if (target.Kind == MovieAcquisitionTargetKind.Unknown && latestMovieGrab == null && latestDownloadGrab == null && historyItems.Any())
                {
                    // Pre-target histories represented ordinary Main implicitly. Keep that
                    // compatibility isolated to durable legacy history reconstruction.
                    target = ReadHistoryTarget(historyItems.First().Data);
                }

                trackedDownload.AcquisitionTarget = target;

                var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItemForTarget(downloadItem.DownloadId, target);
                if (downloadHistory != null)
                {
                    trackedDownload.State = GetStateFromHistory(downloadHistory.EventType);
                }

                var movieGrab = historyItems.FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed && MatchesTarget(h.Data, target));
                var downloadGrab = _downloadHistoryService.GetLatestGrabForTarget(downloadItem.DownloadId, target);
                RehydrateRemoteMovie(trackedDownload, target, historyItems, movieGrab, downloadGrab);

                if (trackedDownload.RemoteMovie == null)
                {
                    _logger.Trace("No Movie found for download '{0}'", trackedDownload.DownloadItem.Title);
                }
            }
            catch (MultipleMoviesFoundException e)
            {
                _logger.Debug(e, "Found multiple movies for " + downloadItem.Title);

                trackedDownload.Warn("Unable to import automatically, found multiple movies: {0}", string.Join(", ", e.Movies));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find movie for " + downloadItem.Title);
                return null;
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);

            _cache.Set(trackedDownload.DownloadItem.DownloadId, trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var untrackable = GetTrackedDownloads().ExceptBy(t => t.DownloadItem.DownloadId, trackedDownloads, t => t.DownloadItem.DownloadId, StringComparer.CurrentCulture).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var target = trackedDownload.AcquisitionTarget;
            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();
            var movieGrab = historyItems.FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed && MatchesTarget(h.Data, target));
            var downloadGrab = _downloadHistoryService.GetLatestGrabForTarget(trackedDownload.DownloadItem.DownloadId, target);
            RehydrateRemoteMovie(trackedDownload, target, historyItems, movieGrab, downloadGrab);
        }

        private void RehydrateRemoteMovie(TrackedDownload trackedDownload,
                                          MovieAcquisitionTarget target,
                                          List<MovieHistory> historyItems,
                                          MovieHistory movieGrab,
                                          DownloadHistory downloadGrab)
        {
            trackedDownload.AcquisitionTarget = target;
            var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title);
            trackedDownload.RemoteMovie = parsedMovieInfo == null ? null : _parsingService.Map(parsedMovieInfo, "", 0, null);

            var sourceHistory = historyItems.FirstOrDefault(h => MatchesTarget(h.Data, target)) ?? historyItems.FirstOrDefault();
            if ((parsedMovieInfo == null || trackedDownload.RemoteMovie?.Movie == null) && sourceHistory != null)
            {
                parsedMovieInfo = Parser.Parser.ParseMovieTitle(sourceHistory.SourceTitle);
                if (parsedMovieInfo != null)
                {
                    trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo, sourceHistory.MovieId);
                }
            }

            var remoteMovie = trackedDownload.RemoteMovie;
            if (target.Kind == MovieAcquisitionTargetKind.Unknown || remoteMovie?.Movie == null)
            {
                trackedDownload.RemoteMovie = null;
                return;
            }

            trackedDownload.Indexer = movieGrab?.Data?.GetValueOrDefault(MovieHistory.INDEXER);
            trackedDownload.Added = movieGrab?.Date;
            remoteMovie.Release ??= downloadGrab?.Release ?? new ReleaseInfo();
            remoteMovie.Release.Indexer = trackedDownload.Indexer ?? remoteMovie.Release.Indexer;
            remoteMovie.Release.Title ??= remoteMovie.ParsedMovieInfo?.ReleaseTitle;
            if (Enum.TryParse(movieGrab?.Data?.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
            {
                remoteMovie.Release.IndexerFlags = flags;
            }
            if (downloadGrab != null)
            {
                remoteMovie.Release.IndexerId = downloadGrab.IndexerId;
            }

            if (target.Kind == MovieAcquisitionTargetKind.EditionSlot)
            {
                var slot = _movieEditionSlotService.GetForMovie(remoteMovie.Movie.Id).SingleOrDefault(x => x.Id == target.EditionSlotId.Value);
                if (slot == null)
                {
                    _logger.Warn("Tracked download targets missing or cross-movie edition slot {0}; leaving it unmapped", target.EditionSlotId.Value);
                    trackedDownload.RemoteMovie = null;
                    return;
                }

                remoteMovie.AcquisitionTarget = target;
                remoteMovie.SlotContextStamped = true;
                remoteMovie.SlotMinimumCustomFormatScore = slot.MinimumCustomFormatScore;
                remoteMovie.SlotQualityProfile = slot.QualityProfileId.HasValue
                    ? _qualityProfileService.Get(slot.QualityProfileId.Value)
                    : remoteMovie.Movie.QualityProfile;
                var slotFile = _mediaFileService.FindByEditionSlotId(slot.Id);
                remoteMovie.SlotMovieFile = slotFile?.MovieId == remoteMovie.Movie.Id && slotFile.MovieEditionSlotId == slot.Id ? slotFile : null;
            }
            else if (target.Kind == MovieAcquisitionTargetKind.Main)
            {
                remoteMovie.AcquisitionTarget = MovieAcquisitionTarget.Main;
                remoteMovie.SlotContextStamped = false;
                remoteMovie.SlotMovieFile = null;
                remoteMovie.SlotQualityProfile = null;
                remoteMovie.SlotMinimumCustomFormatScore = null;
            }

            _aggregationService.Augment(remoteMovie);
            remoteMovie.CustomFormats = _formatCalculator.ParseCustomFormat(remoteMovie, trackedDownload.DownloadItem.TotalSize);
        }

        private static MovieAcquisitionTarget ReadHistoryTarget(IReadOnlyDictionary<string, string> data)
        {
            return data == null ? MovieAcquisitionTarget.Unknown : MovieAcquisitionTargetSerializer.ReadLegacyHistory(data);
        }

        private static MovieAcquisitionTarget ResolveReconstructionTarget(MovieHistory movieGrab, MovieAcquisitionTarget movieTarget, DownloadHistory downloadGrab, MovieAcquisitionTarget downloadTarget)
        {
            if (movieGrab != null && downloadGrab != null && !movieTarget.Equals(downloadTarget))
            {
                return MovieAcquisitionTarget.Unknown;
            }

            return downloadGrab != null ? downloadTarget : movieGrab != null ? movieTarget : MovieAcquisitionTarget.Unknown;
        }

        private static bool MatchesTarget(IReadOnlyDictionary<string, string> data, MovieAcquisitionTarget target)
        {
            return ReadHistoryTarget(data).Equals(target);
        }

        private static TrackedDownloadState GetStateFromHistory(DownloadHistoryEventType eventType)
        {
            switch (eventType)
            {
                case DownloadHistoryEventType.DownloadImported:
                    return TrackedDownloadState.Imported;
                case DownloadHistoryEventType.DownloadFailed:
                    return TrackedDownloadState.Failed;
                case DownloadHistoryEventType.DownloadIgnored:
                    return TrackedDownloadState.Ignored;
                default:
                    return TrackedDownloadState.Downloading;
            }
        }

        private void LogItemChange(TrackedDownload trackedDownload, DownloadClientItem existingItem, DownloadClientItem downloadItem)
        {
            if (existingItem == null ||
                existingItem.Status != downloadItem.Status ||
                existingItem.CanBeRemoved != downloadItem.CanBeRemoved ||
                 existingItem.CanMoveFiles != downloadItem.CanMoveFiles)
            {
                _logger.Debug("Tracking '{0}:{1}': ClientState={2}{3} RadarrStage={4} Movie='{5}' OutputPath={6}.",
                    downloadItem.DownloadClientInfo.Name,
                    downloadItem.Title,
                    downloadItem.Status,
                    downloadItem.CanBeRemoved ? "" : downloadItem.CanMoveFiles ? " (busy)" : " (readonly)",
                    trackedDownload.State,
                    trackedDownload.RemoteMovie?.ParsedMovieInfo,
                    downloadItem.OutputPath);
            }
        }

        public void Handle(MovieAddedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie == null ||
                    message.Movie?.TmdbId == t.RemoteMovie.Movie.TmdbId)
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MovieEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    (t.RemoteMovie.Movie.Id == message.Movie?.Id || t.RemoteMovie.Movie.TmdbId == message.Movie?.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesBulkEditedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }

        public void Handle(MoviesDeletedEvent message)
        {
            var cachedItems = _cache.Values
                .Where(t =>
                    t.RemoteMovie?.Movie != null &&
                    message.Movies.Any(m => m.Id == t.RemoteMovie.Movie.Id || m.TmdbId == t.RemoteMovie.Movie.TmdbId))
                .ToList();

            if (cachedItems.Any())
            {
                cachedItems.ForEach(UpdateCachedItem);

                _eventAggregator.PublishEvent(new TrackedDownloadRefreshedEvent(GetTrackedDownloads()));
            }
        }
    }
}
