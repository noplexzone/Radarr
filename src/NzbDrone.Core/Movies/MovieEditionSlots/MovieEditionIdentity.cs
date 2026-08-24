using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public enum MovieEditionIdentityType
    {
        CanonicalName = 1,
        SearchTerm = 2,
        Alias = 3
    }

    public enum MovieEditionIdentityFindingType
    {
        EmptyNormalizedTerm = 1,
        CrossSlotCollision = 2,
        OrphanAlias = 3,
        MissingMovie = 4
    }

    public class MovieEditionIdentity : ModelBase
    {
        public int MovieId { get; set; }
        public int MovieEditionSlotId { get; set; }
        public string NormalizedTerm { get; set; }
        public MovieEditionIdentityType IdentityType { get; set; }
        public string DisplayValue { get; set; }
    }

    public class MovieEditionIdentityFinding : ModelBase
    {
        public MovieEditionIdentityFindingType FindingType { get; set; }
        public int? MovieId { get; set; }
        public int? MovieEditionSlotId { get; set; }
        public string NormalizedTerm { get; set; }
        public MovieEditionIdentityType? IdentityType { get; set; }
        public string DisplayValue { get; set; }
        public string Details { get; set; }
    }
}
