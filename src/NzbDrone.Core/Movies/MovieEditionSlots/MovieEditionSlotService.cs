using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies.Events;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public interface IMovieEditionSlotService
    {
        List<MovieEditionSlot> GetForMovie(int movieId);
        MovieEditionSlot GetById(int id);
        MovieEditionSlot Add(MovieEditionSlot slot);
        MovieEditionSlot Update(MovieEditionSlot slot);
        void Delete(int id);
        void ReconcileForMovie(int movieId, IReadOnlyList<MovieFile> existingFiles);
        Dictionary<int, (int Monitored, int Missing)> GetSlotStatusSummary(IEnumerable<int> movieIds);
        List<MovieEditionSlot> GetMonitoredMissingSlots();
        List<MovieEditionSlot> GetMonitoredSlotsWithFiles();
        Dictionary<int, MovieEditionSlot> GetByIds(IEnumerable<int> ids);
    }

    public class MovieEditionSlotService : IMovieEditionSlotService,
        IHandleAsync<MoviesDeletedEvent>,
        IHandle<MovieFileAddedEvent>,
        IHandle<MovieFileUpdatedEvent>,
        IHandle<MovieFileImportedEvent>,
        IHandle<MovieFileDeletedEvent>
    {
        private readonly IMovieEditionSlotRepository _repo;
        private readonly IMovieEditionSlotAliasRepository _aliasRepo;
        private readonly IMovieEditionSlotMutationStore _mutationStore;
        private readonly IMediaFileService _mediaFileService;

        public MovieEditionSlotService(IMovieEditionSlotRepository repo,
                                       IMovieEditionSlotAliasRepository aliasRepo,
                                       IMovieEditionSlotMutationStore mutationStore,
                                       IMediaFileService mediaFileService)
        {
            _repo = repo;
            _aliasRepo = aliasRepo;
            _mutationStore = mutationStore;
            _mediaFileService = mediaFileService;
        }

        public List<MovieEditionSlot> GetForMovie(int movieId)
        {
            return LoadAliases(_repo.FindByMovieId(movieId));
        }

        public MovieEditionSlot GetById(int id)
        {
            return LoadAliases(new List<MovieEditionSlot> { _repo.Get(id) }).Single();
        }

        public MovieEditionSlot Add(MovieEditionSlot slot)
        {
            Normalize(slot);
            ValidateTerms(slot);
            if (slot.DateAdded == default)
            {
                slot.DateAdded = DateTime.UtcNow;
            }

            return _mutationStore.Add(slot);
        }

        public MovieEditionSlot Update(MovieEditionSlot slot)
        {
            var persisted = _repo.Get(slot.Id);
            if (persisted.MovieId != slot.MovieId)
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure("MovieId", "An edition cannot be moved to another movie.")
                });
            }

            var attachedFile = _mediaFileService.FindByEditionSlotId(slot.Id);
            if (attachedFile != null && attachedFile.MovieId != persisted.MovieId)
            {
                throw new InvalidOperationException($"Edition '{persisted.EditionName}' is attached to a file from another movie.");
            }

            Normalize(slot);
            ValidateTerms(slot);
            return _mutationStore.Update(slot);
        }

        public void Delete(int id)
        {
            var slot = _repo.Get(id);
            if (_mediaFileService.FindByEditionSlotId(id) != null)
            {
                throw new InvalidOperationException($"Edition '{slot.EditionName}' still has an assigned movie file. Remove it through the movie file assignment service.");
            }

            _mutationStore.Delete(id);
        }

        public Dictionary<int, (int Monitored, int Missing)> GetSlotStatusSummary(IEnumerable<int> movieIds)
        {
            var ids = movieIds?.Distinct().ToList() ?? new List<int>();
            var slots = _repo.FindByMovieIds(ids);
            var attachedSlotIds = _mediaFileService.GetFilesByMovies(ids)
                .Where(f => f.MovieEditionSlotId.HasValue)
                .Select(f => f.MovieEditionSlotId.Value)
                .ToHashSet();

            return slots.GroupBy(s => s.MovieId).ToDictionary(
                g => g.Key,
                g => (g.Count(s => s.Monitored), g.Count(s => s.Monitored && !attachedSlotIds.Contains(s.Id))));
        }

        public List<MovieEditionSlot> GetMonitoredMissingSlots()
        {
            var slots = _repo.All().Where(s => s.Monitored).ToList();
            var files = _mediaFileService.GetFilesByMovies(slots.Select(s => s.MovieId).Distinct());
            var attached = files.Where(f => f.MovieEditionSlotId.HasValue).Select(f => f.MovieEditionSlotId.Value).ToHashSet();
            return LoadAliases(slots.Where(s => !attached.Contains(s.Id)).ToList());
        }

        public List<MovieEditionSlot> GetMonitoredSlotsWithFiles()
        {
            var slots = _repo.All().Where(s => s.Monitored).ToList();
            var files = _mediaFileService.GetFilesByMovies(slots.Select(s => s.MovieId).Distinct());
            var attached = files.Where(f => f.MovieEditionSlotId.HasValue).Select(f => f.MovieEditionSlotId.Value).ToHashSet();
            return LoadAliases(slots.Where(s => attached.Contains(s.Id)).ToList());
        }

        public Dictionary<int, MovieEditionSlot> GetByIds(IEnumerable<int> ids)
        {
            var slotIds = ids?.Distinct().ToList() ?? new List<int>();
            return slotIds.Count == 0
                ? new Dictionary<int, MovieEditionSlot>()
                : LoadAliases(_repo.FindByIds(slotIds)).ToDictionary(s => s.Id);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            foreach (var movie in message.Movies)
            {
                foreach (var slot in _repo.FindByMovieId(movie.Id))
                {
                    _mutationStore.DeleteForMovie(slot.Id);
                }
            }
        }

        // Durable assignments are explicit. Parser/event text never creates slots or remaps files.
        public void Handle(MovieFileAddedEvent message) { }
        public void Handle(MovieFileUpdatedEvent message) { }
        public void Handle(MovieFileImportedEvent message) { }
        public void Handle(MovieFileDeletedEvent message) { }
        public void ReconcileForMovie(int movieId, IReadOnlyList<MovieFile> existingFiles) { }

        private void ValidateTerms(MovieEditionSlot candidate)
        {
            var candidateTerms = GetTerms(candidate).ToHashSet();
            foreach (var existing in LoadAliases(_repo.FindByMovieId(candidate.MovieId)).Where(s => s.Id != candidate.Id))
            {
                if (GetTerms(existing).Any(candidateTerms.Contains))
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("EditionName", $"Edition terms conflict with existing edition '{existing.EditionName}'.")
                    });
                }
            }
        }

        private static IEnumerable<string> GetTerms(MovieEditionSlot slot)
        {
            return new[] { slot.EditionName, slot.SearchTerm }
                .Concat(slot.Aliases ?? new List<string>())
                .Select(EditionNormalizer.Normalize)
                .Where(x => x.IsNotNullOrWhiteSpace());
        }

        private List<MovieEditionSlot> LoadAliases(List<MovieEditionSlot> slots)
        {
            if (slots.Count == 0)
            {
                return slots;
            }

            var aliases = _aliasRepo.FindBySlotIds(slots.Select(s => s.Id)).ToLookup(a => a.MovieEditionSlotId);
            foreach (var slot in slots)
            {
                slot.Aliases = aliases[slot.Id].Select(a => a.Alias).ToList();
            }

            return slots;
        }

        private static void Normalize(MovieEditionSlot slot)
        {
            MovieEditionSlotMutationStore.Normalize(slot);
        }
    }
}
