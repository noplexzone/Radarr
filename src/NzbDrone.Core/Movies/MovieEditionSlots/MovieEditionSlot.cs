using System;
using System.Collections.Generic;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Movies.MovieEditionSlots
{
    public class MovieEditionSlot : ModelBase
    {
        public MovieEditionSlot()
        {
            Aliases = new List<string>();
        }

        public int MovieId { get; set; }
        public string EditionName { get; set; }
        public string CanonicalEditionKey { get; set; }
        public string SearchTerm { get; set; }
        public List<string> Aliases { get; set; }
        public bool Monitored { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MinimumCustomFormatScore { get; set; }
        public DateTime? LastSearchTime { get; set; }
        public DateTime DateAdded { get; set; }
    }

    public class MovieEditionSlotAlias : ModelBase
    {
        public int MovieEditionSlotId { get; set; }
        public string Alias { get; set; }
        public string NormalizedAlias { get; set; }
    }
}
