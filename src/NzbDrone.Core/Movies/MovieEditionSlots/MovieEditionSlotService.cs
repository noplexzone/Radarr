using System;
using System.Collections.Generic;
using NzbDrone.Common.Extensions;
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

    public class MovieEditionSlotService : IMovieEditionSlotService, IHandleAsync<MoviesDeletedEvent>
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

        public void HandleAsync(MoviesDeletedEvent message)
        {
            foreach (var movie in message.Movies)
            {
                _repo.DeleteForMovie(movie.Id);
            }
        }

        private static void Normalize(MovieEditionSlot slot)
        {
            slot.EditionName = slot.EditionName?.Trim() ?? string.Empty;
            slot.SearchTerm = slot.SearchTerm.IsNullOrWhiteSpace() ? null : slot.SearchTerm.Trim();
        }
    }
}
