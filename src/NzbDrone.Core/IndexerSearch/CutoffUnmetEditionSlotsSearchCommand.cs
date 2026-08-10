using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class CutoffUnmetEditionSlotsSearchCommand : Command
    {
        public override bool SendUpdatesToClient => true;
    }
}
