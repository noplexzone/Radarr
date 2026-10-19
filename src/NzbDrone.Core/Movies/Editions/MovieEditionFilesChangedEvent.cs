using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Movies.Editions
{
    // Files were moved between editions, so the movie's main file may have changed
    public class MovieEditionFilesChangedEvent : IEvent
    {
        public int MovieId { get; private set; }

        public MovieEditionFilesChangedEvent(int movieId)
        {
            MovieId = movieId;
        }
    }
}
