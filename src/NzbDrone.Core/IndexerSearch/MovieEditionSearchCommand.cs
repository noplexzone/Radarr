using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieEditionSearchCommand : Command
    {
        private MovieAcquisitionTarget _acquisitionTarget = MovieAcquisitionTarget.Unknown;

        public int MovieId { get; set; }
        public MovieAcquisitionTarget AcquisitionTarget
        {
            get => _acquisitionTarget;
            set => _acquisitionTarget = value ?? MovieAcquisitionTarget.Unknown;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public int? MovieEditionSlotId
        {
            get => AcquisitionTarget?.EditionSlotId;
            set => AcquisitionTarget = value.HasValue ? MovieAcquisitionTarget.ForEditionSlot(value.Value) : MovieAcquisitionTarget.Unknown;
        }
        public List<int> MovieEditionSlotIds { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
