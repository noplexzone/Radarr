using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private const string BackupExtension = ".radarr-upgrade";

        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveMovieFiles _movieFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IMovieEditionService _movieEditionService;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMoveMovieFiles movieFileMover,
                                       IDiskProvider diskProvider,
                                       IMovieEditionService movieEditionService,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _movieFileMover = movieFileMover;
            _diskProvider = diskProvider;
            _movieEditionService = movieEditionService;
            _logger = logger;
        }

        public MovieFileMoveResult UpgradeMovieFile(MovieFile movieFile, LocalMovie localMovie, bool copyOnly = false)
        {
            _logger.Trace("Upgrading movie file.");

            var moveFileResult = new MovieFileMoveResult();

            var existingFile = GetExistingFile(localMovie);

            var rootFolder = _diskProvider.GetParentFolder(localMovie.Movie.Path);

            // If there are existing movie files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFile != null && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            string movieFilePath = null;
            string subfolder = null;
            string backupPath = null;
            DeletedMovieFile deletedFile = null;

            if (existingFile != null)
            {
                movieFilePath = Path.Combine(localMovie.Movie.Path, existingFile.RelativePath);
                subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(movieFilePath));

                if (_diskProvider.FileExists(movieFilePath))
                {
                    // The existing file is only moved aside until the new file is in place, so a failed import never loses it
                    backupPath = movieFilePath + BackupExtension;
                    _logger.Debug("Moving existing movie file aside: {0}", existingFile);
                    _diskProvider.MoveFile(movieFilePath, backupPath, true);
                }
                else
                {
                    _logger.Warn("Existing movie file missing from disk: {0}", movieFilePath);
                }

                deletedFile = new DeletedMovieFile(existingFile, null);
                moveFileResult.OldFiles.Add(deletedFile);
            }

            localMovie.OldFiles = moveFileResult.OldFiles;

            try
            {
                if (copyOnly)
                {
                    moveFileResult.MovieFile = _movieFileMover.CopyMovieFile(movieFile, localMovie);
                }
                else
                {
                    moveFileResult.MovieFile = _movieFileMover.MoveMovieFile(movieFile, localMovie);
                }
            }
            catch
            {
                RestoreBackup(backupPath, movieFilePath);
                moveFileResult.OldFiles.Clear();
                throw;
            }

            if (existingFile != null)
            {
                deletedFile.RecycleBinPath = RecycleExistingFile(backupPath, movieFilePath, subfolder);
                _mediaFileService.Delete(existingFile, DeleteMediaFileReason.Upgrade);
            }

            return moveFileResult;
        }

        private void RestoreBackup(string backupPath, string movieFilePath)
        {
            if (backupPath == null || !_diskProvider.FileExists(backupPath) || _diskProvider.FileExists(movieFilePath))
            {
                return;
            }

            _logger.Debug("Import failed, restoring existing movie file: {0}", movieFilePath);
            _diskProvider.MoveFile(backupPath, movieFilePath);
        }

        private string RecycleExistingFile(string backupPath, string movieFilePath, string subfolder)
        {
            if (backupPath == null)
            {
                return null;
            }

            var path = backupPath;

            // Recycle the old file under its own name unless the new file took it
            if (!_diskProvider.FileExists(movieFilePath))
            {
                _diskProvider.MoveFile(backupPath, movieFilePath);
                path = movieFilePath;
            }

            _logger.Debug("Removing existing movie file: {0}", path);

            return _recycleBinProvider.DeleteFile(path, subfolder);
        }

        private MovieFile GetExistingFile(LocalMovie localMovie)
        {
            if (localMovie.MovieEdition == null)
            {
                return localMovie.Movie.MovieFileId > 0 ? localMovie.Movie.MovieFile : null;
            }

            // Only the edition being imported into is replaced, files of other editions are never touched
            var existingFile = _movieEditionService.GetCurrentFile(localMovie.Movie, localMovie.MovieEdition);

            if (existingFile != null)
            {
                existingFile.Movie = localMovie.Movie;
            }

            return existingFile;
        }
    }
}
