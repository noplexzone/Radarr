using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveMovieFiles _movieFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IMovieEditionSlotService _editionSlotService;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMoveMovieFiles movieFileMover,
                                       IDiskProvider diskProvider,
                                       IMovieEditionSlotService editionSlotService,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieFileMover = movieFileMover;
            _diskProvider = diskProvider;
            _editionSlotService = editionSlotService;
            _logger = logger;
        }

        public MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false)
        {
            _logger.Trace("Upgrading movie file.");

            var moveFileResult = new MovieFileMoveResult();

            ValidateIncomingTarget(movieFile, localMovie);
            var existingFile = ResolveExistingFile(localMovie);
            var existingFilePath = existingFile == null ? null : Path.Combine(localMovie.Movie.Path, existingFile.RelativePath);

            var rootFolder = _diskProvider.GetParentFolder(localMovie.Movie.Path);

            // If there are existing movie files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFile != null && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            _movieFileMover.PreflightMovieFile(movieFile, localMovie, existingFilePath);

            if (existingFile != null)
            {
                var movieFilePath = existingFilePath;
                var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(movieFilePath));
                string recycleBinPath = null;

                if (_diskProvider.FileExists(movieFilePath))
                {
                    _logger.Debug("Removing existing movie file: {0}", existingFile);
                    recycleBinPath = _recycleBinProvider.DeleteFile(movieFilePath, subfolder);
                }
                else
                {
                    _logger.Warn("Existing movie file missing from disk: {0}", movieFilePath);
                }

                moveFileResult.OldFiles.Add(new DeletedMovieFile(existingFile, recycleBinPath));
                _mediaFileService.Delete(existingFile, DeleteMediaFileReason.Upgrade);
            }

            localMovie.OldFiles = moveFileResult.OldFiles;

            if (copyOnly)
            {
                moveFileResult.MovieFile = _movieFileMover.CopyMovieFile(movieFile, localMovie);
            }
            else
            {
                moveFileResult.MovieFile = _movieFileMover.MoveMovieFile(movieFile, localMovie);
            }

            return moveFileResult;
        }

        private static void ValidateIncomingTarget(MovieFile movieFile, LocalMovie localMovie)
        {
            if (movieFile.MovieId != localMovie.Movie.Id)
            {
                throw new InvalidOperationException($"Movie file target {movieFile.MovieId} does not match movie {localMovie.Movie.Id}.");
            }

            if (movieFile.ImportTarget != localMovie.ImportTarget)
            {
                throw new InvalidOperationException("Movie file import target does not match the local movie target.");
            }

            if (localMovie.ImportTarget == MovieFileImportTarget.EditionSlot)
            {
                if (!localMovie.MovieEditionSlotId.HasValue || movieFile.MovieEditionSlotId != localMovie.MovieEditionSlotId)
                {
                    throw new InvalidOperationException("Movie file edition slot does not match the explicit import target.");
                }
            }
            else if (movieFile.MovieEditionSlotId.HasValue)
            {
                throw new InvalidOperationException("Main and unassigned imports cannot carry an edition slot id.");
            }
        }

        private MovieFile ResolveExistingFile(LocalMovie localMovie)
        {
            switch (localMovie.ImportTarget)
            {
                case MovieFileImportTarget.EditionSlot:
                    if (!localMovie.MovieEditionSlotId.HasValue)
                    {
                        throw new InvalidOperationException("An explicit edition slot import requires a slot id.");
                    }

                    var slotId = localMovie.MovieEditionSlotId.Value;
                    var slot = _editionSlotService.GetById(slotId);

                    if (slot == null)
                    {
                        throw new InvalidOperationException($"Edition slot {slotId} does not exist.");
                    }

                    if (slot.MovieId != localMovie.Movie.Id)
                    {
                        throw new InvalidOperationException($"Edition slot {slotId} does not belong to movie {localMovie.Movie.Id}.");
                    }

                    var editionFile = _mediaFileService.FindByEditionSlotId(slotId);
                    if (editionFile != null && editionFile.MovieId != localMovie.Movie.Id)
                    {
                        throw new InvalidOperationException($"Edition slot {slotId} is attached to a file owned by movie {editionFile.MovieId}.");
                    }

                    return editionFile;

                case MovieFileImportTarget.Main:
                    if (localMovie.Movie.MovieFileId <= 0)
                    {
                        return null;
                    }

                    var mainFile = _mediaFileService.GetMovie(localMovie.Movie.MovieFileId);
                    if (mainFile.MovieId != localMovie.Movie.Id || mainFile.MovieEditionSlotId.HasValue)
                    {
                        throw new InvalidOperationException($"Movie file {mainFile.Id} is not a valid main file for movie {localMovie.Movie.Id}.");
                    }

                    return mainFile;

                case MovieFileImportTarget.Unassigned:
                    return null;

                default:
                    throw new InvalidOperationException($"Unsupported movie file import target: {localMovie.ImportTarget}.");
            }
        }
    }
}
