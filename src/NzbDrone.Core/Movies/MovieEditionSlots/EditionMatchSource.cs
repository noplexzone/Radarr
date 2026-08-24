namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public enum EditionMatchSource
    {
        None = 0,
        ParsedMetadata = 1,
        NormalizedTitleFallback = 2
    }
}
