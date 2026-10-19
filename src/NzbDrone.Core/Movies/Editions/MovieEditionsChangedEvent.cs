using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.Movies.Editions
{
    // An edition was added, changed or removed, so anything showing the movie's editions is out of date
    public class MovieEditionsChangedEvent : IEvent
    {
        public int MovieId { get; private set; }

        public MovieEditionsChangedEvent(int movieId)
        {
            MovieId = movieId;
        }
    }
}
