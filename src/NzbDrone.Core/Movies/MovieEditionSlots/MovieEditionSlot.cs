using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public class MovieEditionSlot : ModelBase
    {
        public int MovieId { get; set; }
        public string EditionName { get; set; }
        public string SearchTerm { get; set; }
        public bool Monitored { get; set; }
        public int? MovieFileId { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public DateTime DateAdded { get; set; }
    }
}
