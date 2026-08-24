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
        List<TrackedDownload> TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem);
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
            return _cache.Values.FirstOrDefault(trackedDownload =>
                trackedDownload.DownloadItem.DownloadId.Equals(downloadId, StringComparison.Ordinal));
        }

        public void StopTracking(string downloadId)
        {
            StopTracking(new List<string> { downloadId });
        }

        public void StopTracking(List<string> downloadIds)
        {
            var trackedDownloads = _cache.Values
                .Where(trackedDownload => downloadIds.Contains(trackedDownload.DownloadItem.DownloadId))
                .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _cache.Remove(GetCacheKey(trackedDownload));
            }

            _eventAggregator.PublishEvent(new TrackedDownloadsRemovedEvent(trackedDownloads));
        }

        public List<TrackedDownload> TrackDownload(DownloadClientDefinition downloadClient, DownloadClientItem downloadItem)
        {
            var trackedDownloads = new List<TrackedDownload>();

            try
            {
                var historyItems = _historyService.FindByDownloadId(downloadItem.DownloadId)
                    .OrderByDescending(history => history.Date)
                    .ToList();
                var movieGrabs = historyItems
                    .Where(history => history.EventType == MovieHistoryEventType.Grabbed)
                    .GroupBy(history => new { history.MovieId, Target = ReadHistoryTarget(history.Data) })
                    .Select(group => group.First())
                    .ToList();

                if (movieGrabs.Count == 0)
                {
                    var latestDownloadGrab = _downloadHistoryService.GetLatestGrab(downloadItem.DownloadId);
                    var sourceHistory = historyItems.FirstOrDefault();
                    var target = latestDownloadGrab != null
                        ? ReadHistoryTarget(latestDownloadGrab.Data)
                        : sourceHistory != null
                            ? ReadHistoryTarget(sourceHistory.Data)
                            : MovieAcquisitionTarget.Unknown;
                    var movieId = latestDownloadGrab?.MovieId ?? sourceHistory?.MovieId ?? 0;
                    trackedDownloads.Add(TrackLogicalDownload(downloadClient, downloadItem, movieId, target, sourceHistory, latestDownloadGrab));
                }
                else if (movieGrabs.Count == 1)
                {
                    var movieGrab = movieGrabs[0];
                    var movieTarget = ReadHistoryTarget(movieGrab.Data);
                    var latestDownloadGrab = _downloadHistoryService.GetLatestGrab(downloadItem.DownloadId);
                    var target = ResolveReconstructionTarget(movieGrab, movieTarget, latestDownloadGrab, ReadHistoryTarget(latestDownloadGrab?.Data));
                    trackedDownloads.Add(TrackLogicalDownload(downloadClient, downloadItem, movieGrab.MovieId, target, movieGrab, latestDownloadGrab));
                }
                else
                {
                    foreach (var movieGrab in movieGrabs)
                    {
                        var target = ReadHistoryTarget(movieGrab.Data);
                        var downloadGrab = _downloadHistoryService.GetLatestGrabForTarget(downloadItem.DownloadId, target);
                        trackedDownloads.Add(TrackLogicalDownload(downloadClient, downloadItem, movieGrab.MovieId, target, movieGrab, downloadGrab));
                    }
                }
            }
            catch (MultipleMoviesFoundException e)
            {
                _logger.Debug(e, "Found multiple movies for " + downloadItem.Title);
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to find movie for " + downloadItem.Title);
            }

            return trackedDownloads.Where(trackedDownload => trackedDownload != null).ToList();
        }

        private TrackedDownload TrackLogicalDownload(DownloadClientDefinition downloadClient,
                                                     DownloadClientItem downloadItem,
                                                     int movieId,
                                                     MovieAcquisitionTarget target,
                                                     MovieHistory movieGrab,
                                                     DownloadHistory downloadGrab)
        {
            var cacheKey = GetCacheKey(downloadClient.Id, downloadItem.DownloadId, movieId, target);
            var existingItem = _cache.Find(cacheKey);

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
                MovieId = movieId,
                AcquisitionTarget = target,
                Protocol = downloadClient.Protocol,
                IsTrackable = true,
                HasNotifiedManualInteractionRequired = existingItem?.HasNotifiedManualInteractionRequired ?? false
            };

            var downloadHistory = _downloadHistoryService.GetLatestDownloadHistoryItemForTarget(downloadItem.DownloadId, target);
            if (downloadHistory != null)
            {
                trackedDownload.State = GetStateFromHistory(downloadHistory.EventType);
            }

            RehydrateRemoteMovie(trackedDownload, target, movieGrab, downloadGrab);

            if (trackedDownload.RemoteMovie == null)
            {
                _logger.Trace("No Movie found for download '{0}'", trackedDownload.DownloadItem.Title);
            }

            LogItemChange(trackedDownload, existingItem?.DownloadItem, trackedDownload.DownloadItem);
            _cache.Set(cacheKey, trackedDownload);
            return trackedDownload;
        }

        public List<TrackedDownload> GetTrackedDownloads()
        {
            return _cache.Values.ToList();
        }

        public void UpdateTrackable(List<TrackedDownload> trackedDownloads)
        {
            var trackedKeys = trackedDownloads.Select(GetCacheKey).ToHashSet(StringComparer.Ordinal);
            var untrackable = GetTrackedDownloads().Where(trackedDownload => !trackedKeys.Contains(GetCacheKey(trackedDownload))).ToList();

            foreach (var trackedDownload in untrackable)
            {
                trackedDownload.IsTrackable = false;
            }
        }

        private void UpdateCachedItem(TrackedDownload trackedDownload)
        {
            var target = trackedDownload.AcquisitionTarget;
            var movieGrab = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(history => history.Date)
                .FirstOrDefault(history => history.EventType == MovieHistoryEventType.Grabbed &&
                                           history.MovieId == trackedDownload.MovieId &&
                                           MatchesTarget(history.Data, target));
            var downloadGrab = _downloadHistoryService.GetLatestGrabForTarget(trackedDownload.DownloadItem.DownloadId, target);
            RehydrateRemoteMovie(trackedDownload, target, movieGrab, downloadGrab);
        }

        private void RehydrateRemoteMovie(TrackedDownload trackedDownload,
                                          MovieAcquisitionTarget target,
                                          MovieHistory movieGrab,
                                          DownloadHistory downloadGrab)
        {
            trackedDownload.AcquisitionTarget = target;
            var parsedMovieInfo = Parser.Parser.ParseMovieTitle(trackedDownload.DownloadItem.Title);
            trackedDownload.RemoteMovie = parsedMovieInfo == null ? null : _parsingService.Map(parsedMovieInfo, "", 0, null);

            if ((parsedMovieInfo == null || trackedDownload.RemoteMovie?.Movie == null) && movieGrab != null)
            {
                parsedMovieInfo = Parser.Parser.ParseMovieTitle(movieGrab.SourceTitle);
                if (parsedMovieInfo != null)
                {
                    trackedDownload.RemoteMovie = _parsingService.Map(parsedMovieInfo, movieGrab.MovieId);
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


        private static string GetCacheKey(TrackedDownload trackedDownload)
        {
            return GetCacheKey(trackedDownload.DownloadClient,
                trackedDownload.DownloadItem.DownloadId,
                trackedDownload.MovieId,
                trackedDownload.AcquisitionTarget);
        }

        private static string GetCacheKey(int downloadClientId, string downloadId, int movieId, MovieAcquisitionTarget target)
        {
            return $"{downloadClientId}{downloadId}{movieId}{target.Kind}{target.EditionSlotId}";
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
