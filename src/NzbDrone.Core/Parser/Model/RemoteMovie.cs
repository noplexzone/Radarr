using System.Collections.Generic;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Parser.Model
{
    public class RemoteMovie
    {
        public ReleaseInfo Release { get; set; }
        public ParsedMovieInfo ParsedMovieInfo { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public MovieMatchType MovieMatchType { get; set; }
        public Movie Movie { get; set; }
        public bool MovieRequested { get; set; }
        public bool DownloadAllowed { get; set; }
        public TorrentSeedConfiguration SeedConfiguration { get; set; }
        public List<Language> Languages { get; set; }
        public ReleaseSourceType ReleaseSource { get; set; }
        private MovieAcquisitionTarget _acquisitionTarget = MovieAcquisitionTarget.Main;

        public MovieAcquisitionTarget AcquisitionTarget
        {
            get => _acquisitionTarget;
            set => _acquisitionTarget = value ?? throw new System.ArgumentNullException(nameof(value));
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public int? MovieEditionSlotId
        {
            get => AcquisitionTarget.EditionSlotId;
            set => AcquisitionTarget = value.HasValue ? MovieAcquisitionTarget.ForEditionSlot(value.Value) : MovieAcquisitionTarget.Unknown;
        }

        // Set by DownloadDecisionMaker when an RSS release is matched to a monitored
        // edition slot via parsed edition. When true, SlotMovieFile (not Movie.MovieFile)
        // is the authoritative file for disk-comparison specs.
        public bool SlotContextStamped { get; set; }
        public MovieFile SlotMovieFile { get; set; }
        public QualityProfile SlotQualityProfile { get; set; }
        public int? SlotMinimumCustomFormatScore { get; set; }

        public RemoteMovie()
        {
            CustomFormats = new List<CustomFormat>();
            Languages = new List<Language>();
        }

        public override string ToString()
        {
            return Release.Title;
        }
    }

    public enum ReleaseSourceType
    {
        Unknown = 0,
        Rss = 1,
        Search = 2,
        UserInvokedSearch = 3,
        InteractiveSearch = 4,
        ReleasePush = 5
    }
}
