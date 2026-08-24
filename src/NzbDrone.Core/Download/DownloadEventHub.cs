using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadEventHub : IHandle<DownloadFailedEvent>,
                                    IHandle<DownloadCompletedEvent>,
                                    IHandle<DownloadCanBeRemovedEvent>
    {
        private readonly IPhysicalDownloadFinalizationService _physicalDownloadFinalizationService;

        public DownloadEventHub(IPhysicalDownloadFinalizationService physicalDownloadFinalizationService)
        {
            _physicalDownloadFinalizationService = physicalDownloadFinalizationService;
        }

        public void Handle(DownloadFailedEvent message)
        {
            _physicalDownloadFinalizationService.FinalizeTerminalDownload(message.TrackedDownload);
        }

        public void Handle(DownloadCompletedEvent message)
        {
            _physicalDownloadFinalizationService.FinalizeTerminalDownload(message.TrackedDownload);
        }

        public void Handle(DownloadCanBeRemovedEvent message)
        {
            _physicalDownloadFinalizationService.FinalizeTerminalDownload(message.TrackedDownload);
        }
    }
}
