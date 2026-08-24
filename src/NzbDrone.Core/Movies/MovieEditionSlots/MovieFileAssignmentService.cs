using System;
using System.Collections.Generic;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public enum ExistingMainFileAction
    {
        KeepUnassigned,
        DeleteRecycle,
        Reject
    }

    public enum AttachedEditionFileAction
    {
        DeleteRecycle,
        KeepUnassigned,
        ConvertToMain,
        Cancel
    }

    public interface IMovieFileAssignmentService
    {
        void AssignFileToEdition(int movieId, int movieFileId, int slotId);
        void UnassignEditionFile(int slotId);
        void MakeFileMain(int movieId, int movieFileId, ExistingMainFileAction existingMainAction);
        void RemoveEdition(int slotId, AttachedEditionFileAction attachedFileAction, ExistingMainFileAction? previousMainAction = null);
        MovieFile GetMainFile(int movieId);
        MovieFile GetEditionFile(int slotId);
        List<MovieFile> GetUnassignedFiles(int movieId);
    }

    public class MovieFileAssignmentService : IMovieFileAssignmentService
    {
        private readonly IMovieService _movieService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMovieEditionSlotService _slotService;
        private readonly IMovieEditionSlotMutationStore _mutationStore;
        private readonly IDeleteMediaFiles _deleteMediaFiles;

        public MovieFileAssignmentService(IMovieService movieService,
                                          IMediaFileService mediaFileService,
                                          IMovieEditionSlotService slotService,
                                          IMovieEditionSlotMutationStore mutationStore,
                                          IDeleteMediaFiles deleteMediaFiles)
        {
            _movieService = movieService;
            _mediaFileService = mediaFileService;
            _slotService = slotService;
            _mutationStore = mutationStore;
            _deleteMediaFiles = deleteMediaFiles;
        }

        public void AssignFileToEdition(int movieId, int movieFileId, int slotId)
        {
            var movie = _movieService.GetMovie(movieId);
            var file = _mediaFileService.GetMovie(movieFileId);
            var slot = _slotService.GetById(slotId);

            ValidateMovie(movie, movieId);
            ValidateFile(file, movieFileId);
            ValidateOwnership(movie, file);
            ValidateSlot(slot, slotId, movieId);

            if (movie.MovieFileId == movieFileId)
            {
                throw new InvalidOperationException("A movie file cannot be both the main file and an edition file.");
            }

            if (file.MovieEditionSlotId.HasValue && file.MovieEditionSlotId.Value != slotId)
            {
                throw new InvalidOperationException($"Movie file {movieFileId} is already assigned to edition slot {file.MovieEditionSlotId.Value}.");
            }

            var existing = FindEditionFile(slot);
            if (existing != null && existing.Id != movieFileId)
            {
                throw new InvalidOperationException($"Edition '{slot.EditionName}' already has movie file {existing.Id}.");
            }

            if (file.MovieEditionSlotId == slotId)
            {
                return;
            }

            _mutationStore.AssignFile(movieId, movieFileId, slotId, file.MovieEditionSlotId);
            file.MovieEditionSlotId = slotId;
        }

        public void UnassignEditionFile(int slotId)
        {
            var file = GetEditionFile(slotId);
            if (file == null)
            {
                return;
            }

            UnassignFile(file);
        }

        public void MakeFileMain(int movieId, int movieFileId, ExistingMainFileAction existingMainAction)
        {
            ValidateExistingMainAction(existingMainAction);

            var movie = _movieService.GetMovie(movieId);
            var file = _mediaFileService.GetMovie(movieFileId);
            ValidateMovie(movie, movieId);
            ValidateFile(file, movieFileId);
            ValidateOwnership(movie, file);

            if (file.MovieEditionSlotId.HasValue)
            {
                var targetSlot = _slotService.GetById(file.MovieEditionSlotId.Value);
                ValidateSlot(targetSlot, file.MovieEditionSlotId.Value, movieId);
            }

            MovieFile previousMain = null;
            if (movie.MovieFileId > 0)
            {
                previousMain = _mediaFileService.GetMovie(movie.MovieFileId);
                ValidateFile(previousMain, movie.MovieFileId);
                ValidateOwnership(movie, previousMain);

                if (previousMain.MovieEditionSlotId.HasValue)
                {
                    throw new InvalidOperationException($"Main movie file {previousMain.Id} is also assigned to edition slot {previousMain.MovieEditionSlotId.Value}.");
                }
            }

            if (previousMain != null && previousMain.Id == movieFileId)
            {
                return;
            }

            if (previousMain != null && existingMainAction == ExistingMainFileAction.Reject)
            {
                throw new InvalidOperationException($"Movie {movieId} already has main movie file {previousMain.Id}; an explicit replacement action is required.");
            }

            // TODO: Enlist these durable writes in one datastore unit of work when Radarr exposes a service-layer transaction API.
            if (previousMain != null && existingMainAction == ExistingMainFileAction.DeleteRecycle)
            {
                _deleteMediaFiles.DeleteMovieFile(movie, previousMain);
            }

            if (file.MovieEditionSlotId.HasValue)
            {
                UnassignFile(file);
            }

            movie.MovieFileId = movieFileId;
            _movieService.UpdateMovie(movie);
        }

        public void RemoveEdition(int slotId,
                                  AttachedEditionFileAction attachedFileAction,
                                  ExistingMainFileAction? previousMainAction = null)
        {
            ValidateAttachedFileAction(attachedFileAction);
            if (attachedFileAction == AttachedEditionFileAction.Cancel)
            {
                return;
            }

            if (attachedFileAction == AttachedEditionFileAction.ConvertToMain && !previousMainAction.HasValue)
            {
                throw new InvalidOperationException("Converting an edition file to main requires an explicit action for any existing main file.");
            }

            if (previousMainAction.HasValue)
            {
                ValidateExistingMainAction(previousMainAction.Value);
            }

            var slot = _slotService.GetById(slotId);
            ValidateSlotIdentity(slot, slotId);
            var file = FindEditionFile(slot);

            if (file == null)
            {
                if (attachedFileAction == AttachedEditionFileAction.ConvertToMain)
                {
                    throw new InvalidOperationException($"Edition '{slot.EditionName}' has no assigned movie file to convert to main.");
                }

                _slotService.Delete(slotId);
                return;
            }

            var movie = _movieService.GetMovie(slot.MovieId);
            ValidateMovie(movie, slot.MovieId);
            ValidateOwnership(movie, file);

            switch (attachedFileAction)
            {
                case AttachedEditionFileAction.KeepUnassigned:
                    UnassignFile(file);
                    _slotService.Delete(slotId);
                    break;
                case AttachedEditionFileAction.DeleteRecycle:
                    _deleteMediaFiles.DeleteMovieFile(movie, file);
                    _slotService.Delete(slotId);
                    break;
                case AttachedEditionFileAction.ConvertToMain:
                    MakeFileMain(slot.MovieId, file.Id, previousMainAction.Value);
                    _slotService.Delete(slotId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attachedFileAction), attachedFileAction, null);
            }
        }

        public MovieFile GetMainFile(int movieId)
        {
            var movie = _movieService.GetMovie(movieId);
            ValidateMovie(movie, movieId);
            if (movie.MovieFileId <= 0)
            {
                return null;
            }

            var file = _mediaFileService.GetMovie(movie.MovieFileId);
            ValidateFile(file, movie.MovieFileId);
            ValidateOwnership(movie, file);

            if (file.MovieEditionSlotId.HasValue)
            {
                throw new InvalidOperationException($"Main movie file {file.Id} is also assigned to edition slot {file.MovieEditionSlotId.Value}.");
            }

            return file;
        }

        public MovieFile GetEditionFile(int slotId)
        {
            var slot = _slotService.GetById(slotId);
            ValidateSlotIdentity(slot, slotId);
            return FindEditionFile(slot);
        }

        public List<MovieFile> GetUnassignedFiles(int movieId)
        {
            var movie = _movieService.GetMovie(movieId);
            ValidateMovie(movie, movieId);
            var files = _mediaFileService.GetUnassignedFiles(movieId);

            foreach (var file in files)
            {
                ValidateOwnership(movie, file);
            }

            return files;
        }

        private void UnassignFile(MovieFile file)
        {
            var slotId = file.MovieEditionSlotId ?? throw new InvalidOperationException($"Movie file {file.Id} is not assigned to an edition slot.");
            _mutationStore.UnassignFile(file.Id, slotId);
            file.MovieEditionSlotId = null;
        }

        private MovieFile FindEditionFile(MovieEditionSlot slot)
        {
            var file = _mediaFileService.FindByEditionSlotId(slot.Id);
            if (file != null && file.MovieId != slot.MovieId)
            {
                throw new InvalidOperationException("The assigned movie file and edition belong to different movies.");
            }

            return file;
        }

        private static void ValidateMovie(Movie movie, int movieId)
        {
            if (movie == null || movie.Id != movieId)
            {
                throw new InvalidOperationException($"Movie {movieId} could not be resolved to the requested movie.");
            }
        }

        private static void ValidateFile(MovieFile file, int movieFileId)
        {
            if (file == null || file.Id != movieFileId)
            {
                throw new InvalidOperationException($"Movie file {movieFileId} could not be resolved to the requested file.");
            }
        }

        private static void ValidateSlotIdentity(MovieEditionSlot slot, int slotId)
        {
            if (slot == null || slot.Id != slotId)
            {
                throw new InvalidOperationException($"Edition slot {slotId} could not be resolved to the requested slot.");
            }
        }

        private static void ValidateSlot(MovieEditionSlot slot, int slotId, int movieId)
        {
            ValidateSlotIdentity(slot, slotId);
            if (slot.MovieId != movieId)
            {
                throw new InvalidOperationException($"Edition '{slot.EditionName}' does not belong to movie {movieId}.");
            }
        }

        private static void ValidateOwnership(Movie movie, MovieFile file)
        {
            if (file.MovieId != movie.Id)
            {
                throw new InvalidOperationException($"Movie file {file.Id} belongs to movie {file.MovieId}, not movie {movie.Id}.");
            }
        }

        private static void ValidateExistingMainAction(ExistingMainFileAction action)
        {
            if (!Enum.IsDefined(typeof(ExistingMainFileAction), action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        private static void ValidateAttachedFileAction(AttachedEditionFileAction action)
        {
            if (!Enum.IsDefined(typeof(AttachedEditionFileAction), action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }
    }
}
