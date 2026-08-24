using System.Collections.Generic;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    public class MovieSearchCriteria : SearchCriteriaBase
    {
        private MovieAcquisitionTarget _acquisitionTarget = MovieAcquisitionTarget.Main;

        public MovieAcquisitionTarget AcquisitionTarget
        {
            get => _acquisitionTarget;
            set => _acquisitionTarget = value ?? throw new System.ArgumentNullException(nameof(value));
        }
        public string EditionSearchTerm { get; set; }
        public List<string> EditionMatchTerms { get; set; } = new List<string>();
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public int? MovieEditionSlotId
        {
            get => AcquisitionTarget.EditionSlotId;
            set => AcquisitionTarget = value.HasValue ? MovieAcquisitionTarget.ForEditionSlot(value.Value) : MovieAcquisitionTarget.Unknown;
        }

        // When an edition slot has its own quality profile, this overrides
        // Movie.QualityProfile for quality/custom-format acceptance checks.
        public QualityProfile OverrideQualityProfile { get; set; }

        // Flat minimum custom-format score from the slot (overrides profile's MinFormatScore when set).
        public int? SlotMinimumCustomFormatScore { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}]", Movie.Title);
        }
    }
}
