using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class MovieEditionSearchCommand : Command
    {
        public int MovieId { get; set; }
        public int MovieEditionId { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
