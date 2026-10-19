using System.Collections.Generic;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Movies.Editions
{
    public static class MovieHistoryEditionExtensions
    {
        public const string MovieEditionIdKey = "MovieEditionId";

        public static int GetMovieEditionId(this MovieHistory history)
        {
            int.TryParse(history.Data.GetValueOrDefault("movieEditionId"), out var movieEditionId);

            return movieEditionId;
        }

        // History from before editions existed has no edition and counts as the default edition's
        public static bool IsForEdition(this MovieHistory history, MovieEdition edition)
        {
            if (edition == null)
            {
                return true;
            }

            var movieEditionId = history.GetMovieEditionId();

            return movieEditionId == 0 ? edition.IsDefault : movieEditionId == edition.Id;
        }
    }
}
