using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.MediaFiles
{
    public interface IRenameMovieFileService
    {
        List<RenameMovieFilePreview> GetRenamePreviews(List<int> movieIds);
    }

    public class RenameMovieFileService : IRenameMovieFileService,
                                          IExecute<RenameFilesCommand>,
                                          IExecute<RenameMovieCommand>
    {
        private readonly IMovieService _movieService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMoveMovieFiles _movieFileMover;
        private readonly IEventAggregator _eventAggregator;
        private readonly IBuildFileNames _filenameBuilder;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public RenameMovieFileService(IMovieService movieService,
                                      IMediaFileService mediaFileService,
                                      IMoveMovieFiles movieFileMover,
                                      IEventAggregator eventAggregator,
                                      IBuildFileNames filenameBuilder,
                                      IDiskProvider diskProvider,
                                      Logger logger)
        {
            _movieService = movieService;
            _mediaFileService = mediaFileService;
            _movieFileMover = movieFileMover;
            _eventAggregator = eventAggregator;
            _filenameBuilder = filenameBuilder;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public List<RenameMovieFilePreview> GetRenamePreviews(List<int> movieIds)
        {
            var movies = _movieService.GetMovies(movieIds);
            var movieFiles = _mediaFileService.GetFilesByMovies(movieIds).ToLookup(f => f.MovieId);

            return movies.SelectMany(movie =>
                {
                    var files = movieFiles[movie.Id].ToList();

                    return GetPreviews(movie, files);
                })
                .ToList();
        }

        private IEnumerable<RenameMovieFilePreview> GetPreviews(Movie movie, List<MovieFile> files)
        {
            var collisions = GetCollidingFileIds(movie, files, files);

            foreach (var file in files)
            {
                if (collisions.Contains(file.Id))
                {
                    continue;
                }

                var movieFilePath = Path.Combine(movie.Path, file.RelativePath);

                var newName = _filenameBuilder.BuildFileName(movie, file);
                var newPath = _filenameBuilder.BuildFilePath(movie, newName, Path.GetExtension(movieFilePath));

                if (!movieFilePath.PathEquals(newPath, StringComparison.Ordinal))
                {
                    yield return new RenameMovieFilePreview
                    {
                        MovieId = movie.Id,
                        MovieFileId = file.Id,
                        ExistingPath = file.RelativePath,

                        NewPath = movie.Path.GetRelativePath(newPath)
                    };
                }
            }
        }

        private List<RenamedMovieFile> RenameFiles(List<MovieFile> movieFiles, Movie movie, List<MovieFile> allMovieFiles)
        {
            var renamed = new List<RenamedMovieFile>();
            var collisions = GetCollidingFileIds(movie, movieFiles, allMovieFiles);

            foreach (var movieFile in movieFiles)
            {
                if (collisions.Contains(movieFile.Id))
                {
                    _logger.Warn("File not renamed, its new name would collide with another file of this movie. Add {{Edition Tags}} to the movie file naming format to keep editions apart: {0}", movieFile.RelativePath);
                    continue;
                }

                var previousRelativePath = movieFile.RelativePath;
                var previousPath = Path.Combine(movie.Path, movieFile.RelativePath);

                try
                {
                    _logger.Debug("Renaming movie file: {0}", movieFile);
                    _movieFileMover.MoveMovieFile(movieFile, movie);

                    _mediaFileService.Update(movieFile);
                    _movieService.UpdateMovie(movie);
                    renamed.Add(new RenamedMovieFile
                                {
                                    MovieFile = movieFile,
                                    PreviousRelativePath = previousRelativePath,
                                    PreviousPath = previousPath
                                });

                    _logger.Debug("Renamed movie file: {0}", movieFile);

                    _eventAggregator.PublishEvent(new MovieFileRenamedEvent(movie, movieFile, previousPath));
                }
                catch (FileAlreadyExistsException ex)
                {
                    _logger.Warn("File not renamed, there is already a file at the destination: {0}", ex.Filename);
                }
                catch (SameFilenameException ex)
                {
                    _logger.Debug("File not renamed, source and destination are the same: {0}", ex.Filename);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to rename file: {0}", previousPath);
                }
            }

            if (renamed.Any())
            {
                _diskProvider.RemoveEmptySubfolders(movie.Path);

                _eventAggregator.PublishEvent(new MovieRenamedEvent(movie, renamed));
            }

            return renamed;
        }

        // Several editions share a movie folder, so a naming format without the edition can give two files the same name
        private HashSet<int> GetCollidingFileIds(Movie movie, List<MovieFile> filesToRename, List<MovieFile> allMovieFiles)
        {
            var collisions = new HashSet<int>();

            if (allMovieFiles == null || allMovieFiles.Count < 2)
            {
                return collisions;
            }

            var targets = filesToRename.ToDictionary(f => f.Id, f =>
            {
                var currentPath = Path.Combine(movie.Path, f.RelativePath);
                var newName = _filenameBuilder.BuildFileName(movie, f);

                return _filenameBuilder.BuildFilePath(movie, newName, Path.GetExtension(currentPath));
            });

            foreach (var file in filesToRename)
            {
                var target = targets[file.Id];

                if (target.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var takenByOtherFile = allMovieFiles.Any(f => f.Id != file.Id && PathEqualityComparer.Instance.Equals(Path.Combine(movie.Path, f.RelativePath), target));
                var sharedWithOtherFile = targets.Any(t => t.Key != file.Id && PathEqualityComparer.Instance.Equals(t.Value, target));

                if (takenByOtherFile || sharedWithOtherFile)
                {
                    collisions.Add(file.Id);
                }
            }

            return collisions;
        }

        public void Execute(RenameFilesCommand message)
        {
            var movie = _movieService.GetMovie(message.MovieId);
            var movieFiles = _mediaFileService.GetMovies(message.Files);

            _logger.ProgressInfo("Renaming {0} files for {1}", movieFiles.Count, movie.Title);
            var renamedFiles = RenameFiles(movieFiles, movie, _mediaFileService.GetFilesByMovie(movie.Id));
            _logger.ProgressInfo("{0} selected movie files renamed for {1}", renamedFiles.Count, movie.Title);

            _eventAggregator.PublishEvent(new RenameCompletedEvent());
        }

        public void Execute(RenameMovieCommand message)
        {
            _logger.Debug("Renaming movie files for selected movie");
            var moviesToRename = _movieService.GetMovies(message.MovieIds);

            foreach (var movie in moviesToRename)
            {
                var movieFiles = _mediaFileService.GetFilesByMovie(movie.Id);
                _logger.ProgressInfo("Renaming movie files for {0}", movie.Title);
                var renamedFiles = RenameFiles(movieFiles, movie, movieFiles);
                _logger.ProgressInfo("{0} movie files renamed for {1}", renamedFiles.Count, movie.Title);
            }

            _eventAggregator.PublishEvent(new RenameCompletedEvent());
        }
    }
}
