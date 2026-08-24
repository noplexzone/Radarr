using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDownloadedMovieImportService
    {
        List<ImportResult> ProcessRootFolder(DirectoryInfo directoryInfo);
        List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Movie movie = null, DownloadClientItem downloadClientItem = null);
        List<PhysicalDownloadImportResult> ProcessPhysicalGroup(string path, IReadOnlyList<PhysicalDownloadImportEnvelope> envelopes);
        bool ShouldDeleteFolder(DirectoryInfo directoryInfo, Movie movie);
    }

    public class DownloadedMovieImportService : IDownloadedMovieImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IMovieService _movieService;
        private readonly IParsingService _parsingService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IImportApprovedMovie _importApprovedMovie;
        private readonly IDetectSample _detectSample;
        private readonly IRuntimeInfo _runtimeInfo;
        private readonly IConfigService _config;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IMovieEditionSlotService _movieEditionSlotService;
        private readonly IMovieEditionMatcher _movieEditionMatcher;
        private readonly Logger _logger;

        public DownloadedMovieImportService(IDiskProvider diskProvider,
                                               IDiskScanService diskScanService,
                                               IMovieService movieService,
                                               IParsingService parsingService,
                                               IMakeImportDecision importDecisionMaker,
                                               IImportApprovedMovie importApprovedMovie,
                                               IDetectSample detectSample,
                                               IRuntimeInfo runtimeInfo,
                                               IConfigService config,
                                               IHistoryService historyService,
                                               IDownloadHistoryService downloadHistoryService,
                                               IMovieEditionSlotService movieEditionSlotService,
                                               IMovieEditionMatcher movieEditionMatcher,
                                               Logger logger)
        {
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _movieService = movieService;
            _parsingService = parsingService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedMovie = importApprovedMovie;
            _detectSample = detectSample;
            _runtimeInfo = runtimeInfo;
            _config = config;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _movieEditionSlotService = movieEditionSlotService;
            _movieEditionMatcher = movieEditionMatcher;
            _logger = logger;
        }

        public List<ImportResult> ProcessRootFolder(DirectoryInfo directoryInfo)
        {
            var results = new List<ImportResult>();

            foreach (var subFolder in _diskProvider.GetDirectories(directoryInfo.FullName))
            {
                var folderResults = ProcessFolder(new DirectoryInfo(subFolder), ImportMode.Auto, null);
                results.AddRange(folderResults);
            }

            foreach (var videoFile in _diskScanService.GetVideoFiles(directoryInfo.FullName, false))
            {
                var fileResults = ProcessFile(new FileInfo(videoFile), ImportMode.Auto, null);
                results.AddRange(fileResults);
            }

            return results;
        }

        public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Movie movie = null, DownloadClientItem downloadClientItem = null)
        {
            _logger.Debug("Processing path: {0}", path);

            if (_diskProvider.FolderExists(path))
            {
                var directoryInfo = new DirectoryInfo(path);

                if (movie == null)
                {
                    return ProcessFolder(directoryInfo, importMode, downloadClientItem);
                }

                return ProcessFolder(directoryInfo, importMode, movie, downloadClientItem);
            }

            if (_diskProvider.FileExists(path))
            {
                var fileInfo = new FileInfo(path);

                if (movie == null)
                {
                    return ProcessFile(fileInfo, importMode, downloadClientItem);
                }

                return ProcessFile(fileInfo, importMode, movie, downloadClientItem);
            }

            LogInaccessiblePathError(path);
            return new List<ImportResult>();
        }

        public List<PhysicalDownloadImportResult> ProcessPhysicalGroup(string path, IReadOnlyList<PhysicalDownloadImportEnvelope> envelopes)
        {
            var results = envelopes.Select(envelope => new PhysicalDownloadImportResult(envelope.Key, new List<ImportResult>())).ToList();
            if (envelopes.Count < 2)
            {
                return results;
            }

            List<string> videoFiles;
            ParsedMovieInfo folderInfo = null;
            if (_diskProvider.FolderExists(path))
            {
                if (envelopes.Select(envelope => envelope.Key.MovieId).Distinct().Any(movieId => _movieService.MoviePathExists(path)))
                {
                    return results;
                }

                var directoryInfo = new DirectoryInfo(path);
                folderInfo = Parser.Parser.ParseMovieTitle(GetCleanedUpFolderName(directoryInfo.Name));
                videoFiles = _diskScanService.FilterPaths(path, _diskScanService.GetVideoFiles(path)).ToList();
            }
            else if (_diskProvider.FileExists(path))
            {
                videoFiles = new List<string> { path };
            }
            else
            {
                LogInaccessiblePathError(path);
                return results;
            }

            var envelopeByKey = envelopes.ToDictionary(envelope => envelope.Key);
            var filesByKey = envelopes.ToDictionary(envelope => envelope.Key, _ => new List<string>());
            var activeEnvelopes = envelopes.Where(envelope => envelope.ShouldImport).ToList();
            if (activeEnvelopes.Count == 0)
            {
                return results;
            }

            var histories = _downloadHistoryService.GetHistory(envelopes[0].Key.DownloadId, envelopes[0].Key.DownloadClientId) ?? new List<DownloadHistory>();
            var importedSourcePaths = new HashSet<string>(PathEqualityComparer.Instance);
            foreach (var envelope in envelopes)
            {
                var latestLifecycle = _downloadHistoryService.GetLatestDownloadHistoryItemForTarget(
                    envelope.Key.DownloadId,
                    envelope.Key.DownloadClientId,
                    envelope.Key.MovieId,
                    envelope.Key.AcquisitionTarget);
                if (latestLifecycle?.EventType != DownloadHistoryEventType.DownloadImported &&
                    latestLifecycle?.EventType != DownloadHistoryEventType.FileImported)
                {
                    continue;
                }

                var exactHistory = histories
                    .Where(history => history.DownloadClientId == envelope.Key.DownloadClientId &&
                                      string.Equals(history.DownloadId, envelope.Key.DownloadId, StringComparison.Ordinal) &&
                                      history.MovieId == envelope.Key.MovieId &&
                                      MovieAcquisitionTargetSerializer.Read(history.Data).Equals(envelope.Key.AcquisitionTarget))
                    .ToList();
                var latestGrab = exactHistory
                    .Where(history => history.EventType == DownloadHistoryEventType.DownloadGrabbed)
                    .OrderByDescending(history => history.Date)
                    .FirstOrDefault();
                foreach (var imported in exactHistory.Where(history =>
                             history.EventType == DownloadHistoryEventType.FileImported &&
                             history.SourceTitle.IsNotNullOrWhiteSpace() &&
                             (latestGrab == null || history.Date >= latestGrab.Date)))
                {
                    importedSourcePaths.Add(imported.SourceTitle.CleanFilePath());
                }
            }

            videoFiles = videoFiles.Where(file => !importedSourcePaths.Contains(file.CleanFilePath())).ToList();

            var slotsByMovie = envelopes.Select(envelope => envelope.Key.MovieId)
                .Distinct()
                .ToDictionary(movieId => movieId, movieId => _movieEditionSlotService.GetForMovie(movieId));
            var conflictingPaths = new HashSet<string>(PathEqualityComparer.Instance);

            foreach (var file in videoFiles)
            {
                var claims = new HashSet<TrackedDownloadKey>();
                foreach (var movieGroup in envelopes.GroupBy(envelope => envelope.Key.MovieId))
                {
                    var context = movieGroup.First().RemoteMovie;
                    var candidate = new RemoteMovie
                    {
                        Movie = context.Movie,
                        ParsedMovieInfo = Parser.Parser.ParseMoviePath(file),
                        Release = new ReleaseInfo { Title = Path.GetFileNameWithoutExtension(file) }
                    };
                    var match = _movieEditionMatcher.Match(candidate, slotsByMovie[movieGroup.Key]);
                    var candidateTarget = match.Status == EditionMatchStatus.UniqueSlot
                        ? MovieAcquisitionTarget.ForEditionSlot(match.SelectedSlotId.Value)
                        : match.Status == EditionMatchStatus.NoEditionEvidence
                            ? MovieAcquisitionTarget.Main
                            : null;
                    if (candidateTarget == null)
                    {
                        continue;
                    }

                    foreach (var envelope in movieGroup.Where(item => item.Key.AcquisitionTarget.Equals(candidateTarget)))
                    {
                        claims.Add(envelope.Key);
                    }
                }

                if (claims.Count > 1)
                {
                    conflictingPaths.Add(file.CleanFilePath());
                }

                foreach (var claim in claims)
                {
                    filesByKey[claim].Add(file);
                }
            }

            var decisionsByKey = new Dictionary<TrackedDownloadKey, List<ImportDecision>>();
            foreach (var envelope in activeEnvelopes)
            {
                var decisions = _importDecisionMaker.GetImportDecisions(filesByKey[envelope.Key],
                    envelope.RemoteMovie.Movie, envelope.ImportItem, folderInfo, true);

                for (var index = 0; index < decisions.Count; index++)
                {
                    var localMovie = decisions[index].LocalMovie;
                    if (localMovie == null)
                    {
                        continue;
                    }

                    localMovie.AcquisitionTarget = envelope.Key.AcquisitionTarget;
                    localMovie.Release = GetExactGrabbedRelease(envelope);
                    localMovie.TargetQualityProfile = envelope.Key.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot
                        ? envelope.RemoteMovie.SlotQualityProfile
                        : envelope.RemoteMovie.Movie.QualityProfile;
                    localMovie.TargetMovieFile = envelope.Key.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot
                        ? envelope.RemoteMovie.SlotMovieFile
                        : envelope.RemoteMovie.Movie.MovieFile;
                    localMovie.TargetMinimumCustomFormatScore = envelope.Key.AcquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot
                        ? envelope.RemoteMovie.SlotMinimumCustomFormatScore
                        : null;
                    localMovie.HasExactTargetContext = true;
                    localMovie.CustomFormatScore = localMovie.TargetQualityProfile?.CalculateCustomFormatScore(localMovie.CustomFormats) ?? 0;
                    decisions[index] = conflictingPaths.Contains(localMovie.Path.CleanFilePath())
                        ? new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.InvalidFilePath, "Source path was claimed by more than one exact import target"))
                        : _importDecisionMaker.GetDecision(localMovie, envelope.ImportItem);
                }

                decisionsByKey[envelope.Key] = decisions;
            }

            var combinedDecisions = decisionsByKey.Values.SelectMany(decisions => decisions).ToList();
            var combinedResults = _importApprovedMovie.Import(combinedDecisions, true, activeEnvelopes[0].ImportItem, ImportMode.Copy);
            foreach (var importResult in combinedResults)
            {
                var localMovie = importResult.ImportDecision.LocalMovie;
                if (localMovie == null)
                {
                    continue;
                }

                var matchingKey = envelopeByKey.Keys.SingleOrDefault(key =>
                    key.MovieId == localMovie.Movie?.Id && key.AcquisitionTarget.Equals(localMovie.AcquisitionTarget));
                if (matchingKey != null)
                {
                    results.Single(result => result.Key == matchingKey).ImportResults.Add(importResult);
                }
            }

            return results;
        }

        private GrabbedReleaseInfo GetExactGrabbedRelease(PhysicalDownloadImportEnvelope envelope)
        {
            var history = _downloadHistoryService.GetLatestGrabForTarget(envelope.Key.DownloadId,
                envelope.Key.DownloadClientId, envelope.Key.MovieId, envelope.Key.AcquisitionTarget);
            return history == null ? null : new GrabbedReleaseInfo(history);
        }

        public bool ShouldDeleteFolder(DirectoryInfo directoryInfo, Movie movie)
        {
            try
            {
                var videoFiles = _diskScanService.GetVideoFiles(directoryInfo.FullName);
                var rarFiles = _diskProvider.GetFiles(directoryInfo.FullName, true).Where(f =>
                    Path.GetExtension(f).Equals(".rar",
                        StringComparison.OrdinalIgnoreCase));

                foreach (var videoFile in videoFiles)
                {
                    var movieParseResult =
                        Parser.Parser.ParseMovieTitle(Path.GetFileName(videoFile));

                    if (movieParseResult == null)
                    {
                        _logger.Warn("Unable to parse file on import: [{0}]", videoFile);
                        return false;
                    }

                    if (_detectSample.IsSample(movie.MovieMetadata, videoFile) != DetectSampleResult.Sample)
                    {
                        _logger.Warn("Non-sample file detected: [{0}]", videoFile);
                        return false;
                    }
                }

                if (rarFiles.Any(f => _diskProvider.GetFileSize(f) > 10.Megabytes()))
                {
                    _logger.Warn("RAR file detected, will require manual cleanup");
                    return false;
                }

                return true;
            }
            catch (DirectoryNotFoundException e)
            {
                _logger.Debug(e, "Folder {0} has already been removed", directoryInfo.FullName);
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Unable to determine whether folder {0} should be removed", directoryInfo.FullName);
                return false;
            }
        }

        private List<ImportResult> ProcessFolder(DirectoryInfo directoryInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var cleanedUpName = GetCleanedUpFolderName(directoryInfo.Name);
            var movie = _parsingService.GetMovie(cleanedUpName);

            if (movie == null)
            {
                _logger.Debug("Unknown Movie {0}", cleanedUpName);

                return new List<ImportResult>
                       {
                           UnknownMovieResult("Unknown Movie")
                       };
            }

            return ProcessFolder(directoryInfo, importMode, movie, downloadClientItem);
        }

        private List<ImportResult> ProcessFolder(DirectoryInfo directoryInfo, ImportMode importMode, Movie movie, DownloadClientItem downloadClientItem)
        {
            if (_movieService.MoviePathExists(directoryInfo.FullName))
            {
                _logger.Warn("Unable to process folder that is mapped to an existing movie");
                return new List<ImportResult>
                {
                    RejectionResult(ImportRejectionReason.MovieFolder, "Import path is mapped to a movie folder")
                };
            }

            var cleanedUpName = GetCleanedUpFolderName(directoryInfo.Name);
            var historyItems = _historyService.FindByDownloadId(downloadClientItem?.DownloadId ?? "");
            var firstHistoryItem = historyItems?.OrderByDescending(h => h.Date).FirstOrDefault();
            var folderInfo = Parser.Parser.ParseMovieTitle(cleanedUpName);

            if (folderInfo != null)
            {
                _logger.Debug("{0} folder quality: {1}", cleanedUpName, folderInfo.Quality);
            }

            var videoFiles = _diskScanService.FilterPaths(directoryInfo.FullName, _diskScanService.GetVideoFiles(directoryInfo.FullName));

            if (downloadClientItem == null)
            {
                foreach (var videoFile in videoFiles)
                {
                    if (_diskProvider.IsFileLocked(videoFile))
                    {
                        return new List<ImportResult>
                               {
                                   FileIsLockedResult(videoFile)
                               };
                    }
                }
            }

            var decisions = _importDecisionMaker.GetImportDecisions(videoFiles.ToList(), movie, downloadClientItem, folderInfo, true);
            var importResults = _importApprovedMovie.Import(decisions, true, downloadClientItem, importMode);

            if (importMode == ImportMode.Auto)
            {
                importMode = (downloadClientItem == null || downloadClientItem.CanMoveFiles) ? ImportMode.Move : ImportMode.Copy;
            }

            if (importMode == ImportMode.Move &&
                importResults.Any(i => i.Result == ImportResultType.Imported) &&
                ShouldDeleteFolder(directoryInfo, movie))
            {
                _logger.Debug("Deleting folder after importing valid files");

                try
                {
                    _diskProvider.DeleteFolder(directoryInfo.FullName, true);
                }
                catch (IOException e)
                {
                    _logger.Debug(e, "Unable to delete folder after importing: {0}", e.Message);
                }
            }
            else if (importResults.Empty())
            {
                importResults.AddIfNotNull(CheckEmptyResultForIssue(directoryInfo.FullName));
            }

            return importResults;
        }

        private List<ImportResult> ProcessFile(FileInfo fileInfo, ImportMode importMode, DownloadClientItem downloadClientItem)
        {
            var movie = _parsingService.GetMovie(Path.GetFileNameWithoutExtension(fileInfo.Name));

            if (movie == null)
            {
                _logger.Debug("Unknown Movie for file: {0}", fileInfo.Name);

                return new List<ImportResult>
                       {
                           UnknownMovieResult(string.Format("Unknown Movie for file: {0}", fileInfo.Name), fileInfo.FullName)
                       };
            }

            return ProcessFile(fileInfo, importMode, movie, downloadClientItem);
        }

        private List<ImportResult> ProcessFile(FileInfo fileInfo, ImportMode importMode, Movie movie, DownloadClientItem downloadClientItem)
        {
            if (Path.GetFileNameWithoutExtension(fileInfo.Name).StartsWith("._"))
            {
                _logger.Debug("[{0}] starts with '._', skipping", fileInfo.FullName);

                return new List<ImportResult>
                       {
                           new ImportResult(new ImportDecision(new LocalMovie { Path = fileInfo.FullName }, new ImportRejection(ImportRejectionReason.InvalidFilePath, "Invalid video file, filename starts with '._'")), "Invalid video file, filename starts with '._'")
                       };
            }

            var extension = Path.GetExtension(fileInfo.Name);

            if (FileExtensions.DangerousExtensions.Contains(extension))
            {
                return new List<ImportResult>
                {
                    new ImportResult(new ImportDecision(new LocalMovie { Path = fileInfo.FullName },
                            new ImportRejection(ImportRejectionReason.DangerousFile, $"Caution: Found potentially dangerous file with extension: {extension}")),
                        $"Caution: Found potentially dangerous file with extension: {extension}")
                };
            }

            if (FileExtensions.ExecutableExtensions.Contains(extension))
            {
                return new List<ImportResult>
                {
                    new ImportResult(new ImportDecision(new LocalMovie { Path = fileInfo.FullName },
                            new ImportRejection(ImportRejectionReason.ExecutableFile, $"Caution: Found executable file with extension: '{extension}'")),
                        $"Caution: Found executable file with extension: '{extension}'")
                };
            }

            if (extension.IsNullOrWhiteSpace() || !MediaFileExtensions.Extensions.Contains(extension))
            {
                _logger.Debug("[{0}] has an unsupported extension: '{1}'", fileInfo.FullName, extension);

                return new List<ImportResult>
                       {
                           new ImportResult(new ImportDecision(new LocalMovie { Path = fileInfo.FullName },
                               new ImportRejection(ImportRejectionReason.UnsupportedExtension, $"Invalid video file, unsupported extension: '{extension}'")),
                               $"Invalid video file, unsupported extension: '{extension}'")
                       };
            }

            if (downloadClientItem == null)
            {
                if (_diskProvider.IsFileLocked(fileInfo.FullName))
                {
                    return new List<ImportResult>
                           {
                               FileIsLockedResult(fileInfo.FullName)
                           };
                }
            }

            var decisions = _importDecisionMaker.GetImportDecisions(new List<string>() { fileInfo.FullName }, movie, downloadClientItem, null, true);

            return _importApprovedMovie.Import(decisions, true, downloadClientItem, importMode);
        }

        private string GetCleanedUpFolderName(string folder)
        {
            folder = folder.Replace("_UNPACK_", "")
                           .Replace("_FAILED_", "");

            return folder;
        }

        private ImportResult FileIsLockedResult(string videoFile)
        {
            _logger.Debug("[{0}] is currently locked by another process, skipping", videoFile);
            return new ImportResult(new ImportDecision(new LocalMovie { Path = videoFile }, new ImportRejection(ImportRejectionReason.FileLocked, "Locked file, try again later")), "Locked file, try again later");
        }

        private ImportResult UnknownMovieResult(string message, string videoFile = null)
        {
            var localMovie = videoFile == null ? null : new LocalMovie { Path = videoFile };

            return new ImportResult(new ImportDecision(localMovie, new ImportRejection(ImportRejectionReason.UnknownMovie, "Unknown Movie")), message);
        }

        private ImportResult RejectionResult(ImportRejectionReason reason, string message)
        {
            return new ImportResult(new ImportDecision(null, new ImportRejection(reason, message)), message);
        }

        private ImportResult CheckEmptyResultForIssue(string folder)
        {
            var files = _diskProvider.GetFiles(folder, true);

            if (files.Any(file => FileExtensions.DangerousExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.DangerousFile, "Caution: Found potentially dangerous file");
            }

            if (files.Any(file => FileExtensions.ExecutableExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.ExecutableFile, "Caution: Found executable file");
            }

            if (files.Any(file => FileExtensions.ArchiveExtensions.Contains(Path.GetExtension(file))))
            {
                return RejectionResult(ImportRejectionReason.ArchiveFile, "Found archive file, might need to be extracted");
            }

            return null;
        }

        private void LogInaccessiblePathError(string path)
        {
            if (_runtimeInfo.IsWindowsService)
            {
                var mounts = _diskProvider.GetMounts();
                var mount = mounts.FirstOrDefault(m => m.RootDirectory == Path.GetPathRoot(path));

                if (mount == null)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Radarr: {0}. Unable to find a volume mounted for the path. If you're using a mapped network drive see the FAQ for more info", path);
                    return;
                }

                if (mount.DriveType == DriveType.Network)
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Radarr: {0}. It's recommended to avoid mapped network drives when running as a Windows service. See the FAQ for more info", path);
                    return;
                }
            }

            if (OsInfo.IsWindows)
            {
                if (path.StartsWith(@"\\"))
                {
                    _logger.Error("Import failed, path does not exist or is not accessible by Radarr: {0}. Ensure the user running Radarr has access to the network share", path);
                    return;
                }
            }

            _logger.Error("Import failed, path does not exist or is not accessible by Radarr: {0}. Ensure the path exists and the user running Radarr has the correct permissions to access this file/folder", path);
        }
    }
}
