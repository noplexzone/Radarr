using System.Collections.Generic;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
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

        // The edition the release is for, null when it couldn't be determined (see MovieEditionMatch)
        public MovieEdition MovieEdition { get; set; }
        public MovieEditionMatch MovieEditionMatch { get; set; }

        // The current file of the release's edition
        public MovieFile EditionFile { get; set; }

        private QualityProfile _qualityProfile;

        // The edition's quality profile override, otherwise the movie's
        public QualityProfile QualityProfile
        {
            get => _qualityProfile ?? Movie?.QualityProfile;
            set => _qualityProfile = value;
        }

        // The file a release is compared with: the edition's file, or the movie's file when the edition is unknown
        public MovieFile ExistingFile => MovieEdition != null ? EditionFile : Movie?.MovieFile;

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
