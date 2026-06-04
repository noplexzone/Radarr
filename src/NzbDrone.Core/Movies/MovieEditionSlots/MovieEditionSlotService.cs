using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    }

    public class MovieEditionSlotService : IMovieEditionSlotService,
        IHandleAsync<MoviesDeletedEvent>,
        IHandle<MovieFileAddedEvent>,
        IHandle<MovieFileUpdatedEvent>,
        IHandle<MovieFileImportedEvent>,
        IHandle<MovieFileDeletedEvent>
    {
        private static readonly Regex NormalizeEditionRegex = new Regex(@"[\s\-_.']+", RegexOptions.Compiled);
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

        private void LinkMovieFileToEditionSlot(MovieFile movieFile)
        {
            if (movieFile == null || movieFile.MovieId <= 0 || movieFile.Edition.IsNullOrWhiteSpace())
            {
                return;
            }

            var slots = _repo.FindByMovieId(movieFile.MovieId);
            var normalizedEdition = NormalizeEditionForMatch(movieFile.Edition);
            var matchingSlot = slots.FirstOrDefault(slot =>
                NormalizeEditionForMatch(slot.EditionName) == normalizedEdition ||
                NormalizeEditionForMatch(slot.SearchTerm) == normalizedEdition);

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

        private static string NormalizeEditionForMatch(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            return NormalizeEditionRegex.Replace(value, string.Empty).ToLowerInvariant();
        }

        private static void Normalize(MovieEditionSlot slot)
        {
            slot.EditionName = slot.EditionName?.Trim() ?? string.Empty;
            slot.SearchTerm = slot.SearchTerm.IsNullOrWhiteSpace() ? null : slot.SearchTerm.Trim();
        }
    }
}
