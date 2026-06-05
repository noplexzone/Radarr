using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static readonly Regex NormalizeEditionRegex = new Regex(@"[\s\-_.']+", RegexOptions.Compiled);

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

            var existingFile = ResolveExistingFile(localMovie);

            var rootFolder = _diskProvider.GetParentFolder(localMovie.Movie.Path);

            // If there are existing movie files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFile != null && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            if (existingFile != null)
            {
                var movieFilePath = Path.Combine(localMovie.Movie.Path, existingFile.RelativePath);
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

        // When importing an edition-specific file, only replace the file that belongs to the
        // matching edition slot — never touch a different slot's file.
        // Slot ID (from grab history) wins over fuzzy edition-name matching.
        private MovieFile ResolveExistingFile(LocalMovie localMovie)
        {
            if (localMovie.MovieEditionSlotId.HasValue || localMovie.Edition.IsNotNullOrWhiteSpace())
            {
                var slots = _editionSlotService.GetForMovie(localMovie.Movie.Id);

                MovieEditionSlot matchingSlot = null;

                if (localMovie.MovieEditionSlotId.HasValue)
                {
                    matchingSlot = slots.FirstOrDefault(s => s.Id == localMovie.MovieEditionSlotId.Value);

                    if (matchingSlot == null)
                    {
                        _logger.Warn("Movie edition slot {0} was specified for import of '{1}', but no matching slot exists for movie {2}; no existing file will be replaced", localMovie.MovieEditionSlotId.Value, localMovie.Path, localMovie.Movie.Id);
                        return null;
                    }
                }

                if (matchingSlot == null && localMovie.Edition.IsNotNullOrWhiteSpace())
                {
                    var normalizedEdition = NormalizeEdition(localMovie.Edition);
                    matchingSlot = slots.FirstOrDefault(s =>
                        NormalizeEdition(s.EditionName) == normalizedEdition ||
                        NormalizeEdition(s.SearchTerm) == normalizedEdition);
                }

                if (matchingSlot?.MovieFileId > 0)
                {
                    return _mediaFileService.GetMovie(matchingSlot.MovieFileId.Value);
                }

                return null;
            }

            return localMovie.Movie.MovieFileId > 0 ? localMovie.Movie.MovieFile : null;
        }

        private static string NormalizeEdition(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return NormalizeEditionRegex.Replace(value, string.Empty).ToLowerInvariant();
        }
    }
}
