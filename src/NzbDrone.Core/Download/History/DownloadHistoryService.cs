using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.History
{
    public interface IDownloadHistoryService
    {
        bool DownloadAlreadyImported(string downloadId);
        DownloadHistory GetLatestDownloadHistoryItem(string downloadId);
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        DownloadHistory GetLatestDownloadHistoryItem(string downloadId, int? movieEditionSlotId);
        DownloadHistory GetLatestDownloadHistoryItemForTarget(string downloadId, MovieAcquisitionTarget target);
        DownloadHistory GetLatestDownloadHistoryItemForTarget(string downloadId, int downloadClientId, int movieId, MovieAcquisitionTarget target);
        DownloadHistory GetLatestGrab(string downloadId);
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        DownloadHistory GetLatestGrab(string downloadId, int? movieEditionSlotId);
        DownloadHistory GetLatestGrabForTarget(string downloadId, MovieAcquisitionTarget target);
        DownloadHistory GetLatestGrabForTarget(string downloadId, int downloadClientId, int movieId, MovieAcquisitionTarget target);
        List<DownloadHistory> GetGrabs(string downloadId, int downloadClientId);
    }

    public class DownloadHistoryService : IDownloadHistoryService,
                                          IHandle<MovieGrabbedEvent>,
                                          IHandle<MovieFileImportedEvent>,
                                          IHandle<DownloadCompletedEvent>,
                                          IHandle<DownloadFailedEvent>,
                                          IHandle<DownloadIgnoredEvent>,
                                          IHandle<MoviesDeletedEvent>
    {
        private readonly IDownloadHistoryRepository _repository;
        private readonly IHistoryService _historyService;

        public DownloadHistoryService(IDownloadHistoryRepository repository, IHistoryService historyService)
        {
            _repository = repository;
            _historyService = historyService;
        }

        public bool DownloadAlreadyImported(string downloadId)
        {
            var events = _repository.FindByDownloadId(downloadId);

            // Events are ordered by date descending, if a grabbed event comes before an imported event then it was never imported
            // or grabbed again after importing and should be reprocessed.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return false;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return true;
                }
            }

            return false;
        }

        public DownloadHistory GetLatestDownloadHistoryItem(string downloadId)
        {
            var events = _repository.FindByDownloadId(downloadId);

            // Events are ordered by date descending. We'll return the most recent expected event.
            foreach (var e in events)
            {
                if (e.EventType == DownloadHistoryEventType.DownloadIgnored)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadGrabbed)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadImported)
                {
                    return e;
                }

                if (e.EventType == DownloadHistoryEventType.DownloadFailed)
                {
                    return e;
                }
            }

            return null;
        }

        public DownloadHistory GetLatestDownloadHistoryItemForTarget(string downloadId, MovieAcquisitionTarget target)
        {
            return _repository.FindByDownloadId(downloadId)
                .Where(history => MatchesTarget(history, target))
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadIgnored ||
                                           history.EventType == DownloadHistoryEventType.DownloadGrabbed ||
                                           history.EventType == DownloadHistoryEventType.DownloadImported ||
                                           history.EventType == DownloadHistoryEventType.DownloadFailed);
        }

        public DownloadHistory GetLatestDownloadHistoryItemForTarget(string downloadId, int downloadClientId, int movieId, MovieAcquisitionTarget target)
        {
            return _repository.FindByDownloadId(downloadId)
                .Where(history => MatchesIdentity(history, downloadClientId, movieId, target))
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadIgnored ||
                                           history.EventType == DownloadHistoryEventType.DownloadGrabbed ||
                                           history.EventType == DownloadHistoryEventType.DownloadImported ||
                                           history.EventType == DownloadHistoryEventType.DownloadFailed);
        }

        public DownloadHistory GetLatestGrab(string downloadId)
        {
            return _repository.FindByDownloadId(downloadId)
                              .FirstOrDefault(d => d.EventType == DownloadHistoryEventType.DownloadGrabbed);
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public DownloadHistory GetLatestDownloadHistoryItem(string downloadId, int? movieEditionSlotId)
        {
            var target = FromLegacyNullableTarget(movieEditionSlotId);
            return _repository.FindByDownloadId(downloadId)
                .Where(history => MatchesLegacyTarget(history, target))
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadIgnored ||
                                           history.EventType == DownloadHistoryEventType.DownloadGrabbed ||
                                           history.EventType == DownloadHistoryEventType.DownloadImported ||
                                           history.EventType == DownloadHistoryEventType.DownloadFailed);
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public DownloadHistory GetLatestGrab(string downloadId, int? movieEditionSlotId)
        {
            var target = FromLegacyNullableTarget(movieEditionSlotId);
            return _repository.FindByDownloadId(downloadId)
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadGrabbed && MatchesLegacyTarget(history, target));
        }

        public DownloadHistory GetLatestGrabForTarget(string downloadId, MovieAcquisitionTarget target)
        {
            return _repository.FindByDownloadId(downloadId)
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadGrabbed && MatchesTarget(history, target));
        }

        public DownloadHistory GetLatestGrabForTarget(string downloadId, int downloadClientId, int movieId, MovieAcquisitionTarget target)
        {
            return _repository.FindByDownloadId(downloadId)
                .FirstOrDefault(history => history.EventType == DownloadHistoryEventType.DownloadGrabbed &&
                                           MatchesIdentity(history, downloadClientId, movieId, target));
        }

        public List<DownloadHistory> GetGrabs(string downloadId, int downloadClientId)
        {
            return _repository.FindByDownloadId(downloadId)
                .Where(history => history.EventType == DownloadHistoryEventType.DownloadGrabbed && history.DownloadClientId == downloadClientId)
                .ToList();
        }

        public void Handle(MovieGrabbedEvent message)
        {
            // Don't store grabbed events for clients that don't download IDs
            if (message.DownloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                MovieId = message.Movie.Movie.Id,
                DownloadId = message.DownloadId,
                SourceTitle = message.Movie.Release.Title,
                Date = DateTime.UtcNow,
                Protocol = message.Movie.Release.DownloadProtocol,
                IndexerId = message.Movie.Release.IndexerId,
                DownloadClientId = message.DownloadClientId,
                Release = message.Movie.Release
            };

            history.Data.Add("Indexer", message.Movie.Release.Indexer);
            history.Data.Add("DownloadClient", message.DownloadClient);
            history.Data.Add("DownloadClientName", message.DownloadClientName);

            history.Data.Add("CustomFormatScore", message.Movie.CustomFormatScore.ToString());

            MovieAcquisitionTargetSerializer.Write(history.Data, message.Movie.AcquisitionTarget);

            _repository.Insert(history);
        }

        public void Handle(MovieFileImportedEvent message)
        {
            if (!message.NewDownload)
            {
                return;
            }

            var downloadId = message.DownloadId;

            // Try to find the downloadId if the user used manual import (from wanted: missing) or the
            // API to import and downloadId wasn't provided.
            if (downloadId.IsNullOrWhiteSpace())
            {
                downloadId = _historyService.FindDownloadId(message);
            }

            if (downloadId.IsNullOrWhiteSpace())
            {
                return;
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported,
                MovieId = message.ImportedMovie.MovieId,
                DownloadId = downloadId,
                SourceTitle = message.MovieInfo.Path,
                Date = DateTime.UtcNow,
                Protocol = message.DownloadClientInfo.Protocol,
                DownloadClientId = message.DownloadClientInfo.Id
            };

            history.Data.Add("DownloadClient", message.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", message.DownloadClientInfo.Name);
            AddAcquisitionTarget(history, ResolveImportedTarget(message.ImportedMovie, message.MovieInfo));

            _repository.Insert(history);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            var downloadItem = message.TrackedDownload.DownloadItem;

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadImported,
                MovieId = message.MovieId,
                DownloadId = downloadItem.DownloadId,
                SourceTitle = downloadItem.Title,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", downloadItem.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", downloadItem.DownloadClientInfo.Name);
            AddAcquisitionTarget(history, message.TrackedDownload.AcquisitionTarget);

            _repository.Insert(history);
        }

        public void Handle(DownloadFailedEvent message)
        {
            // Don't track failed download for an unknown download
            if (message.TrackedDownload == null)
            {
                return;
            }

            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadFailed,
                MovieId = message.MovieId,
                DownloadId = message.DownloadId,
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = message.TrackedDownload.Protocol,
                DownloadClientId = message.TrackedDownload.DownloadClient
            };

            history.Data.Add("DownloadClient", message.TrackedDownload.DownloadItem.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", message.TrackedDownload.DownloadItem.DownloadClientInfo.Name);
            AddAcquisitionTarget(history, message.AcquisitionTarget);

            _repository.Insert(history);
        }

        public void Handle(DownloadIgnoredEvent message)
        {
            var history = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadIgnored,
                MovieId = message.MovieId,
                DownloadId = message.DownloadId,
                SourceTitle = message.SourceTitle,
                Date = DateTime.UtcNow,
                Protocol = message.DownloadClientInfo.Protocol,
                DownloadClientId = message.DownloadClientInfo.Id
            };

            history.Data.Add("DownloadClient", message.DownloadClientInfo.Type);
            history.Data.Add("DownloadClientName", message.DownloadClientInfo.Name);
            AddAcquisitionTarget(history, ResolveTrackedTarget(message.TrackedDownload));

            _repository.Insert(history);
        }

        private static bool MatchesTarget(DownloadHistory history, MovieAcquisitionTarget target)
        {
            return MovieAcquisitionTargetSerializer.Read(history.Data).Equals(target);
        }

        private static bool MatchesLegacyTarget(DownloadHistory history, MovieAcquisitionTarget target)
        {
            return MovieAcquisitionTargetSerializer.ReadLegacyHistory(history.Data).Equals(target);
        }

        private static bool MatchesIdentity(DownloadHistory history, int downloadClientId, int movieId, MovieAcquisitionTarget target)
        {
            return history.DownloadClientId == downloadClientId &&
                   history.MovieId == movieId &&
                   MatchesTarget(history, target);
        }

        private static MovieAcquisitionTarget FromLegacyNullableTarget(int? movieEditionSlotId)
        {
            return movieEditionSlotId.HasValue
                ? MovieAcquisitionTarget.ForEditionSlot(movieEditionSlotId.Value)
                : MovieAcquisitionTarget.Main;
        }

        private static MovieAcquisitionTarget ResolveImportedTarget(MovieFile movieFile, LocalMovie localMovie)
        {
            if (movieFile.MovieEditionSlotId.HasValue)
            {
                return MovieAcquisitionTarget.ForEditionSlot(movieFile.MovieEditionSlotId.Value);
            }

            if (movieFile.ImportTarget == MovieFileImportTarget.Main)
            {
                return MovieAcquisitionTarget.Main;
            }

            if (movieFile.ImportTarget == MovieFileImportTarget.Unassigned ||
                localMovie?.ImportTarget == MovieFileImportTarget.Unassigned ||
                localMovie?.ImportTarget == MovieFileImportTarget.Unknown)
            {
                return MovieAcquisitionTarget.Unknown;
            }

            return localMovie?.AcquisitionTarget ?? MovieAcquisitionTarget.Unknown;
        }

        private static MovieAcquisitionTarget ResolveTrackedTarget(NzbDrone.Core.Download.TrackedDownloads.TrackedDownload trackedDownload)
        {
            return trackedDownload?.AcquisitionTarget ?? MovieAcquisitionTarget.Unknown;
        }

        private static void AddAcquisitionTarget(DownloadHistory history, MovieAcquisitionTarget target)
        {
            MovieAcquisitionTargetSerializer.Write(history.Data, target ?? MovieAcquisitionTarget.Unknown);
        }

        public void Handle(MoviesDeletedEvent message)
        {
            _repository.DeleteByMovieIds(message.Movies.Select(m => m.Id).ToList());
        }
    }
}
