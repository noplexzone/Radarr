using System;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.TrackedDownloads
{
    public sealed record TrackedDownloadKey(int DownloadClientId, string DownloadId, int MovieId, MovieAcquisitionTarget AcquisitionTarget)
    {
        public bool IsValid => !string.IsNullOrWhiteSpace(DownloadId) && AcquisitionTarget != null;
    }

    public class TrackedDownload
    {
        public int DownloadClient { get; set; }
        public DownloadClientItem DownloadItem { get; set; }
        public DownloadClientItem ImportItem { get; set; }
        public TrackedDownloadState State { get; set; }
        public TrackedDownloadStatus Status { get; private set; }
        public RemoteMovie RemoteMovie { get; set; }
        public int MovieId { get; set; }
        private MovieAcquisitionTarget _acquisitionTarget = MovieAcquisitionTarget.Unknown;

        public MovieAcquisitionTarget AcquisitionTarget
        {
            get => _acquisitionTarget;
            set => _acquisitionTarget = value ?? throw new ArgumentNullException(nameof(value));
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public int? MovieEditionSlotId
        {
            get => AcquisitionTarget.EditionSlotId;
            set => AcquisitionTarget = value.HasValue ? MovieAcquisitionTarget.ForEditionSlot(value.Value) : MovieAcquisitionTarget.Unknown;
        }
        public TrackedDownloadStatusMessage[] StatusMessages { get; private set; }
        public DownloadProtocol Protocol { get; set; }
        public string Indexer { get; set; }
        public DateTime? Added { get; set; }
        public bool IsTrackable { get; set; }
        public bool HasNotifiedManualInteractionRequired { get; set; }
        public TrackedDownloadKey Key => new (DownloadClient, DownloadItem.DownloadId, MovieId, AcquisitionTarget);

        public TrackedDownload()
        {
            StatusMessages = Array.Empty<TrackedDownloadStatusMessage>();
        }

        public void Warn(string message, params object[] args)
        {
            var statusMessage = string.Format(message, args);
            Warn(new TrackedDownloadStatusMessage(DownloadItem.Title, statusMessage));
        }

        public void Warn(params TrackedDownloadStatusMessage[] statusMessages)
        {
            Status = TrackedDownloadStatus.Warning;
            StatusMessages = statusMessages;
        }

        public void Fail()
        {
            Status = TrackedDownloadStatus.Error;
            State = TrackedDownloadState.FailedPending;

            // Set CanBeRemoved to allow the failed item to be removed from the client
            DownloadItem.CanBeRemoved = true;
        }
    }

    public enum TrackedDownloadState
    {
        Downloading,
        ImportBlocked,
        ImportPending,
        Importing,
        Imported,
        FailedPending,
        Failed,
        Ignored
    }

    public enum TrackedDownloadStatus
    {
        Ok,
        Warning,
        Error
    }
}
