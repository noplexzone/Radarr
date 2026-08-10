using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IImportApprovedMovie
    {
        List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto);
    }

    public class ImportApprovedMovie : IImportApprovedMovie
    {
        private readonly IUpgradeMediaFiles _movieFileUpgrader;
        private readonly IMediaFileService _mediaFileService;
        private readonly IExtraService _extraService;
        private readonly IExistingExtraFiles _existingExtraFiles;
        private readonly IDiskProvider _diskProvider;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IMovieEditionSlotService _movieEditionSlotService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        public ImportApprovedMovie(IUpgradeMediaFiles movieFileUpgrader,
                                   IMediaFileService mediaFileService,
                                   IExtraService extraService,
                                   IExistingExtraFiles existingExtraFiles,
                                   IDiskProvider diskProvider,
                                   IHistoryService historyService,
                                   IEventAggregator eventAggregator,
                                   IManageCommandQueue commandQueueManager,
                                   IMovieEditionSlotService movieEditionSlotService,
                                   IQualityProfileService qualityProfileService,
                                   Logger logger)
        {
            _movieFileUpgrader = movieFileUpgrader;
            _mediaFileService = mediaFileService;
            _extraService = extraService;
            _existingExtraFiles = existingExtraFiles;
            _diskProvider = diskProvider;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _commandQueueManager = commandQueueManager;
            _movieEditionSlotService = movieEditionSlotService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
        }

        public List<ImportResult> Import(List<ImportDecision> decisions, bool newDownload, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto)
        {
            _logger.Debug("Decisions: {0}", decisions.Count);

            var importResults = new List<ImportResult>();
            var validatedImports = new List<(ImportDecision Decision, MovieEditionSlot Slot)>();
            var slots = new Dictionary<int, MovieEditionSlot>();

            foreach (var decision in decisions.Where(decision => decision.Approved))
            {
                try
                {
                    MovieEditionSlot slot = null;
                    var localMovie = decision.LocalMovie;
                    if (localMovie.ImportTarget == MovieFileImportTarget.EditionSlot)
                    {
                        if (!localMovie.MovieEditionSlotId.HasValue)
                        {
                            throw new InvalidOperationException("An explicit edition slot import requires a slot id.");
                        }

                        var slotId = localMovie.MovieEditionSlotId.Value;
                        if (!slots.TryGetValue(slotId, out slot))
                        {
                            slot = _movieEditionSlotService.GetById(slotId);
                            slots.Add(slotId, slot);
                        }

                        if (slot.MovieId != localMovie.Movie.Id)
                        {
                            throw new InvalidOperationException($"Edition slot {slotId} does not belong to movie {localMovie.Movie.Id}.");
                        }
                    }

                    validatedImports.Add((decision, slot));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Couldn't validate durable import target for {0}", decision.LocalMovie);
                    importResults.Add(new ImportResult(decision, "Failed to import movie, invalid edition slot target."));
                }
            }

            // A parsed edition label is descriptive only. Deduplication follows the durable target.
            var qualifiedImports = validatedImports
                .GroupBy(item => (item.Decision.LocalMovie.Movie.Id,
                                  item.Decision.LocalMovie.ImportTarget,
                                  GetTargetSlotId(item.Decision.LocalMovie)))
                .SelectMany(group =>
                {
                    var slot = group.First().Slot;
                    var profile = slot?.QualityProfileId is int profileId
                        ? _qualityProfileService.Get(profileId)
                        : group.First().Decision.LocalMovie.Movie.QualityProfile;
                    var minimumScore = slot?.MinimumCustomFormatScore;

                    return group
                        .OrderByDescending(item => !minimumScore.HasValue || profile.CalculateCustomFormatScore(item.Decision.LocalMovie.CustomFormats) >= minimumScore.Value)
                        .ThenByDescending(item => item.Decision.LocalMovie.Quality ?? new QualityModel { Quality = Quality.Unknown }, new QualityModelComparer(profile))
                        .ThenByDescending(item => item.Decision.LocalMovie.Size)
                        .Select(item => item.Decision);
                })
                .ToList();

            foreach (var importDecision in qualifiedImports)
            {
                var localMovie = importDecision.LocalMovie;
                var oldFiles = new List<DeletedMovieFile>();

                try
                {
                    if (importResults.Any(r =>
                            r.Result == ImportResultType.Imported &&
                            r.ImportDecision.LocalMovie.Movie.Id == localMovie.Movie.Id &&
                            r.ImportDecision.LocalMovie.ImportTarget == localMovie.ImportTarget &&
                            GetTargetSlotId(r.ImportDecision.LocalMovie) == GetTargetSlotId(localMovie)))
                    {
                        importResults.Add(new ImportResult(importDecision, "Movie has already been imported"));
                        continue;
                    }

                    var movieFile = new MovieFile();
                    movieFile.DateAdded = DateTime.UtcNow;
                    movieFile.MovieId = localMovie.Movie.Id;
                    movieFile.Path = localMovie.Path.CleanFilePath();
                    movieFile.Size = _diskProvider.GetFileSize(localMovie.Path);
                    movieFile.Quality = localMovie.Quality;
                    movieFile.Languages = localMovie.Languages;
                    movieFile.MediaInfo = localMovie.MediaInfo;
                    movieFile.Movie = localMovie.Movie;
                    movieFile.ReleaseGroup = localMovie.ReleaseGroup;
                    movieFile.Edition = localMovie.Edition;
                    movieFile.MovieEditionSlotId = localMovie.ImportTarget == MovieFileImportTarget.EditionSlot
                        ? localMovie.MovieEditionSlotId
                        : null;
                    movieFile.ImportTarget = localMovie.ImportTarget;

                    if (downloadClientItem?.DownloadId.IsNotNullOrWhiteSpace() == true)
                    {
                        var grabHistory = _historyService.FindByDownloadId(downloadClientItem.DownloadId)
                            .OrderByDescending(h => h.Date)
                            .FirstOrDefault(h => h.EventType == MovieHistoryEventType.Grabbed);

                        if (Enum.TryParse(grabHistory?.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                        {
                            movieFile.IndexerFlags = flags;
                        }
                    }
                    else
                    {
                        movieFile.IndexerFlags = localMovie.IndexerFlags;
                    }

                    bool copyOnly;
                    switch (importMode)
                    {
                        default:
                        case ImportMode.Auto:
                            copyOnly = downloadClientItem is { CanMoveFiles: false };
                            break;
                        case ImportMode.Move:
                            copyOnly = false;
                            break;
                        case ImportMode.Copy:
                            copyOnly = true;
                            break;
                    }

                    if (newDownload)
                    {
                        movieFile.SceneName = localMovie.SceneName;
                        movieFile.OriginalFilePath = GetOriginalFilePath(downloadClientItem, localMovie);

                        oldFiles = _movieFileUpgrader.UpgradeMovieFile(movieFile, localMovie, copyOnly).OldFiles;
                    }
                    else
                    {
                        movieFile.RelativePath = localMovie.Movie.Path.GetRelativePath(movieFile.Path);

                        // Delete existing files from the DB mapped to this path
                        var previousFiles = _mediaFileService.GetFilesWithRelativePath(localMovie.Movie.Id, movieFile.RelativePath);

                        foreach (var previousFile in previousFiles)
                        {
                            _mediaFileService.Delete(previousFile, DeleteMediaFileReason.ManualOverride);
                        }
                    }

                    movieFile = _mediaFileService.Add(movieFile);
                    importResults.Add(new ImportResult(importDecision));

                    if (localMovie.ImportTarget == MovieFileImportTarget.Main)
                    {
                        localMovie.Movie.MovieFile = movieFile;
                    }

                    if (newDownload)
                    {
                        if (localMovie.ScriptImported)
                        {
                            _existingExtraFiles.ImportExtraFiles(localMovie.Movie, localMovie.PossibleExtraFiles, localMovie.FileNameBeforeRename);

                            if (localMovie.FileNameBeforeRename != movieFile.RelativePath)
                            {
                                _extraService.MoveFilesAfterRename(localMovie.Movie, movieFile);
                            }
                        }

                        if (!localMovie.ScriptImported || localMovie.ShouldImportExtras)
                        {
                            _extraService.ImportMovie(localMovie, movieFile, copyOnly);
                        }
                    }

                    _eventAggregator.PublishEvent(new MovieFileImportedEvent(localMovie, movieFile, oldFiles, newDownload, downloadClientItem));
                }
                catch (RootFolderNotFoundException e)
                {
                    _logger.Warn(e, "Couldn't import movie " + localMovie);
                    _eventAggregator.PublishEvent(new MovieImportFailedEvent(e, localMovie, newDownload, downloadClientItem));

                    importResults.Add(new ImportResult(importDecision, "Failed to import movie, Root folder missing."));
                }
                catch (DestinationAlreadyExistsException e)
                {
                    _logger.Warn(e, "Couldn't import movie " + localMovie);
                    importResults.Add(new ImportResult(importDecision, "Failed to import movie, Destination already exists."));

                    _commandQueueManager.Push(new RescanMovieCommand(localMovie.Movie.Id));
                }
                catch (RecycleBinException e)
                {
                    _logger.Warn(e, "Couldn't import movie " + localMovie);
                    _eventAggregator.PublishEvent(new MovieImportFailedEvent(e, localMovie, newDownload, downloadClientItem));

                    importResults.Add(new ImportResult(importDecision, "Failed to import movie, unable to move existing file to the Recycle Bin."));
                }
                catch (Exception e)
                {
                    _logger.Warn(e, "Couldn't import movie " + localMovie);
                    importResults.Add(new ImportResult(importDecision, "Failed to import movie"));
                }
            }

            // Adding all the rejected decisions
            importResults.AddRange(decisions.Where(c => !c.Approved)
                                            .Select(d => new ImportResult(d, d.Rejections.Select(r => r.Message).ToArray())));

            return importResults;
        }

        private static int? GetTargetSlotId(LocalMovie localMovie)
        {
            return localMovie.ImportTarget == MovieFileImportTarget.EditionSlot
                ? localMovie.MovieEditionSlotId
                : null;
        }

        private string GetOriginalFilePath(DownloadClientItem downloadClientItem, LocalMovie localMovie)
        {
            var path = localMovie.Path;

            if (downloadClientItem != null && !downloadClientItem.OutputPath.IsEmpty)
            {
                var outputDirectory = downloadClientItem.OutputPath.Directory.ToString();

                if (outputDirectory.IsParentPath(path))
                {
                    return outputDirectory.GetRelativePath(path);
                }
            }

            var folderMovieInfo = localMovie.FolderMovieInfo;

            if (folderMovieInfo != null)
            {
                var folderPath = path.GetAncestorPath(folderMovieInfo.OriginalTitle);

                if (folderPath != null)
                {
                    return folderPath.GetParentPath().GetRelativePath(path);
                }
            }

            var parentPath = path.GetParentPath();
            var grandparentPath = parentPath.GetParentPath();

            if (grandparentPath != null)
            {
                return grandparentPath.GetRelativePath(path);
            }

            return Path.GetFileName(path);
        }
    }
}
