using System;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download.Pending
{
    public class PendingRelease : ModelBase
    {
        public int MovieId { get; set; }
        public string Title { get; set; }
        public DateTime Added { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public ReleaseInfo Release { get; set; }
        public PendingReleaseReason Reason { get; set; }
        public PendingReleaseAdditionalInfo AdditionalInfo { get; set; }

        // Not persisted
        public RemoteMovie RemoteMovie { get; set; }
    }

    public class PendingReleaseAdditionalInfo
    {
        public MovieMatchType MovieMatchType { get; set; }
        public ReleaseSourceType ReleaseSource { get; set; }
        public MovieAcquisitionTargetKind? AcquisitionTargetKind { get; set; }
        public int? AcquisitionTargetEditionSlotId { get; set; }

        public void SerializeAcquisitionTarget(MovieAcquisitionTarget target)
        {
            target ??= MovieAcquisitionTarget.Unknown;
            AcquisitionTargetKind = target.Kind;
            AcquisitionTargetEditionSlotId = target.EditionSlotId;
        }

        public static MovieAcquisitionTarget DeserializeAcquisitionTarget(PendingReleaseAdditionalInfo additionalInfo)
        {
            if (additionalInfo == null || !additionalInfo.AcquisitionTargetKind.HasValue)
            {
                // All pre-feature pending rows represented Main; legacy absence maps to Main only here.
                return additionalInfo?.AcquisitionTargetEditionSlotId.HasValue == true
                    ? MovieAcquisitionTarget.Unknown
                    : MovieAcquisitionTarget.Main;
            }

            return additionalInfo.AcquisitionTargetKind switch
            {
                MovieAcquisitionTargetKind.Main when !additionalInfo.AcquisitionTargetEditionSlotId.HasValue => MovieAcquisitionTarget.Main,
                MovieAcquisitionTargetKind.EditionSlot when additionalInfo.AcquisitionTargetEditionSlotId is > 0 => MovieAcquisitionTarget.ForEditionSlot(additionalInfo.AcquisitionTargetEditionSlotId.Value),
                MovieAcquisitionTargetKind.Unknown when !additionalInfo.AcquisitionTargetEditionSlotId.HasValue => MovieAcquisitionTarget.Unknown,
                _ => MovieAcquisitionTarget.Unknown
            };
        }
    }
}
