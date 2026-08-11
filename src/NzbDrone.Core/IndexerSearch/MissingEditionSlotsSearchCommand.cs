using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class MissingEditionSlotsSearchCommand : Command
    {
        public override bool SendUpdatesToClient => true;
    }
}
