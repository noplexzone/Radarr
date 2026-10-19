using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Movies.Editions
{
    public class MovieEditionExistsException : NzbDroneException
    {
        public MovieEdition Edition { get; }

        public MovieEditionExistsException(MovieEdition edition)
            : base("Edition '{0}' already exists for this movie", edition.DisplayTitle)
        {
            Edition = edition;
        }
    }
}
