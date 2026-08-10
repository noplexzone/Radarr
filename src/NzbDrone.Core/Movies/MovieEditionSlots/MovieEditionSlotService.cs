using System;
using System.Collections.Generic;
using System.Linq;
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

        // Bulk helpers used by search commands and movie list enrichment
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

        public MovieEditionSlotService(IMovieEditionSlotRepository repo)
        {
            _repo = repo;
        }

        public List<MovieEditionSlot> GetForMovie(int movieId)
        {
            return _repo.FindByMovieId(movieId);
        }

        public MovieEditionSlot GetById(int id)
        {
            return _repo.Get(id);
        }

        public MovieEditionSlot Add(MovieEditionSlot slot)
        {
            Normalize(slot);

            if (slot.DateAdded == default)
            {
                slot.DateAdded = DateTime.UtcNow;
            }

            return _repo.Insert(slot);
        }

        public MovieEditionSlot Update(MovieEditionSlot slot)
        {
            Normalize(slot);
            return _repo.Update(slot);
        }

        public void Delete(int id)
        {
            _repo.Delete(id);
        }

        public Dictionary<int, (int Monitored, int Missing)> GetSlotStatusSummary(IEnumerable<int> movieIds)
        {
            var slots = _repo.FindByMovieIds(movieIds);
            return slots
                .GroupBy(s => s.MovieId)
                .ToDictionary(
                    g => g.Key,
                    g => (
                        Monitored: g.Count(s => s.Monitored),
                        Missing: g.Count(s => s.Monitored && !s.MovieFileId.HasValue)
                    ));
        }

        public List<MovieEditionSlot> GetMonitoredMissingSlots()
        {
            return _repo.FindMonitoredWithoutFiles();
        }

        public List<MovieEditionSlot> GetMonitoredSlotsWithFiles()
        {
            return _repo.FindMonitoredWithFiles();
        }

        public Dictionary<int, MovieEditionSlot> GetByIds(IEnumerable<int> ids)
        {
            var slotIds = ids?.Distinct().ToList() ?? new List<int>();

            if (!slotIds.Any())
            {
                return new Dictionary<int, MovieEditionSlot>();
            }

            return _repo.FindByIds(slotIds).ToDictionary(s => s.Id);
        }

        public void HandleAsync(MoviesDeletedEvent message)
        {
            foreach (var movie in message.Movies)
            {
                _repo.DeleteForMovie(movie.Id);
            }
        }

        public void Handle(MovieFileAddedEvent message)
        {
            LinkMovieFileToEditionSlot(message.MovieFile);
        }

        public void Handle(MovieFileUpdatedEvent message)
        {
            LinkMovieFileToEditionSlot(message.MovieFile);
        }

        public void Handle(MovieFileImportedEvent message)
        {
            LinkMovieFileToEditionSlot(message.ImportedMovie);
        }

        public void Handle(MovieFileDeletedEvent message)
        {
            if (message.MovieFile == null)
            {
                return;
            }

            foreach (var slot in _repo.FindByMovieFileId(message.MovieFile.Id))
            {
                slot.MovieFileId = null;
                _repo.Update(slot);
            }
        }

        public void ReconcileForMovie(int movieId, IReadOnlyList<MovieFile> existingFiles)
        {
            var slots = _repo.FindByMovieId(movieId);
            if (slots.Count == 0)
            {
                return;
            }

            var fileIds = new HashSet<int>(existingFiles.Select(f => f.Id));

            // Clear slot references whose file no longer exists (belt-and-suspenders;
            // MovieFileDeletedEvent normally handles this during cleanup).
            foreach (var slot in slots.Where(s => s.MovieFileId.HasValue && !fileIds.Contains(s.MovieFileId.Value)))
            {
                slot.MovieFileId = null;
                _repo.Update(slot);
            }

            // Build set of file IDs already linked to a slot so we skip them.
            var linkedFileIds = new HashSet<int>(slots.Where(s => s.MovieFileId.HasValue).Select(s => s.MovieFileId.Value));

            // Link each unlinked edition file to its matching slot (conservative: no slot creation).
            foreach (var file in existingFiles.Where(f => !f.Edition.IsNullOrWhiteSpace() && !linkedFileIds.Contains(f.Id)))
            {
                var normalizedEdition = EditionNormalizer.Normalize(file.Edition);
                var matchingSlot = slots.FirstOrDefault(s =>
                    s.MovieFileId == null &&
                    (EditionNormalizer.Normalize(s.EditionName) == normalizedEdition ||
                     EditionNormalizer.Normalize(s.SearchTerm) == normalizedEdition));

                if (matchingSlot == null)
                {
                    continue;
                }

                matchingSlot.MovieFileId = file.Id;
                linkedFileIds.Add(file.Id);
                _repo.Update(matchingSlot);
            }
        }

        private void LinkMovieFileToEditionSlot(MovieFile movieFile)
        {
            if (movieFile == null || movieFile.MovieId <= 0 || movieFile.Edition.IsNullOrWhiteSpace())
            {
                return;
            }

            var slots = _repo.FindByMovieId(movieFile.MovieId);
            var normalizedEdition = EditionNormalizer.Normalize(movieFile.Edition);
            var matchingSlot = slots.FirstOrDefault(slot =>
                EditionNormalizer.Normalize(slot.EditionName) == normalizedEdition ||
                EditionNormalizer.Normalize(slot.SearchTerm) == normalizedEdition);

            if (matchingSlot == null)
            {
                Add(new MovieEditionSlot
                {
                    MovieId = movieFile.MovieId,
                    EditionName = movieFile.Edition,
                    SearchTerm = movieFile.Edition,
                    Monitored = true,
                    MovieFileId = movieFile.Id
                });

                return;
            }

            matchingSlot.MovieFileId = movieFile.Id;
            _repo.Update(matchingSlot);
        }

        private static void Normalize(MovieEditionSlot slot)
        {
            slot.EditionName = slot.EditionName?.Trim() ?? string.Empty;
            slot.SearchTerm = slot.SearchTerm.IsNullOrWhiteSpace() ? null : slot.SearchTerm.Trim();
        }
    }
}
