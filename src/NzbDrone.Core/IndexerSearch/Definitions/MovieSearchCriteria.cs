namespace NzbDrone.Core.IndexerSearch.Definitions
{
    public class MovieSearchCriteria : SearchCriteriaBase
    {
        public string EditionSearchTerm { get; set; }
        public int? MovieEditionSlotId { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}]", Movie.Title);
        }
    }
}
