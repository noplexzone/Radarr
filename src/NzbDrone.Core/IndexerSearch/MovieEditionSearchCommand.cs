using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieEditionSearchCommand : Command
    {
        public int MovieId { get; set; }
        public int? MovieEditionSlotId { get; set; }
        public List<int> MovieEditionSlotIds { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
