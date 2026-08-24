using System.Collections.Generic;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Core.Parser.Model
{
    public class LocalMovie
    {
        public LocalMovie()
        {
            CustomFormats = new List<CustomFormat>();
            ImportTarget = MovieFileImportTarget.Main;
        }

        private MovieAcquisitionTarget _acquisitionTarget = MovieAcquisitionTarget.Main;
        private MovieFileImportTarget _importTarget = MovieFileImportTarget.Main;

        public string Path { get; set; }
        public long Size { get; set; }
        public ParsedMovieInfo FileMovieInfo { get; set; }
        public ParsedMovieInfo DownloadClientMovieInfo { get; set; }
        public DownloadClientItem DownloadItem { get; set; }
        public ParsedMovieInfo FolderMovieInfo { get; set; }
        public Movie Movie { get; set; }
        public List<DeletedMovieFile> OldFiles { get; set; }
        public QualityModel Quality { get; set; }
        public List<Language> Languages { get; set; }
        public IndexerFlags IndexerFlags { get; set; }
        public MediaInfoModel MediaInfo { get; set; }
        public bool ExistingFile { get; set; }
        public bool SceneSource { get; set; }
        public string ReleaseGroup { get; set; }
        public string Edition { get; set; }
        public MovieFileImportTarget ImportTarget
        {
            get => _importTarget;
            set
            {
                _importTarget = value;
                _acquisitionTarget = value == MovieFileImportTarget.Main
                    ? MovieAcquisitionTarget.Main
                    : value == MovieFileImportTarget.EditionSlot && _acquisitionTarget.Kind == MovieAcquisitionTargetKind.EditionSlot
                        ? _acquisitionTarget
                        : MovieAcquisitionTarget.Unknown;
            }
        }

        public MovieAcquisitionTarget AcquisitionTarget
        {
            get => _acquisitionTarget;
            set
            {
                _acquisitionTarget = value ?? throw new System.ArgumentNullException(nameof(value));
                if (value.Kind == MovieAcquisitionTargetKind.Main)
                {
                    _importTarget = MovieFileImportTarget.Main;
                }
                else if (value.Kind == MovieAcquisitionTargetKind.EditionSlot)
                {
                    _importTarget = MovieFileImportTarget.EditionSlot;
                }
                else if (_importTarget != MovieFileImportTarget.Unassigned)
                {
                    _importTarget = MovieFileImportTarget.Unknown;
                }
            }
        }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public int? MovieEditionSlotId
        {
            get => AcquisitionTarget.EditionSlotId;
            set
            {
                if (value.HasValue)
                {
                    AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(value.Value);
                }
                else
                {
                    _acquisitionTarget = _importTarget == MovieFileImportTarget.Main
                        ? MovieAcquisitionTarget.Main
                        : MovieAcquisitionTarget.Unknown;
                    if (_importTarget == MovieFileImportTarget.EditionSlot)
                    {
                        _importTarget = MovieFileImportTarget.Unknown;
                    }
                }
            }
        }
        public string SceneName { get; set; }
        public bool OtherVideoFiles { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public QualityProfile TargetQualityProfile { get; set; }
        public MovieFile TargetMovieFile { get; set; }
        public int? TargetMinimumCustomFormatScore { get; set; }
        public bool HasExactTargetContext { get; set; }
        public GrabbedReleaseInfo Release { get; set; }
        public bool ScriptImported { get; set; }
        public string FileNameBeforeRename { get; set; }
        public bool ShouldImportExtras { get; set; }
        public List<string> PossibleExtraFiles { get; set; }
        public SubtitleTitleInfo SubtitleInfo { get; set; }

        public override string ToString()
        {
            return Path;
        }
    }

    public enum MovieFileImportTarget
    {
        Unknown = 0,
        Main = 1,
        EditionSlot = 2,
        Unassigned = 3
    }
}
