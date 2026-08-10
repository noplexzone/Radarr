using System.Collections.Generic;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Parser.Model
{
    public class LocalMovie
    {
        public LocalMovie()
        {
            CustomFormats = new List<CustomFormat>();
            ImportTarget = MovieFileImportTarget.Main;
        }

        private int? _movieEditionSlotId;
        private MovieFileImportTarget _importTarget;

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
                if (value != MovieFileImportTarget.EditionSlot)
                {
                    _movieEditionSlotId = null;
                }
            }
        }
        public int? MovieEditionSlotId
        {
            get => _movieEditionSlotId;
            set
            {
                _movieEditionSlotId = value;
                if (value.HasValue)
                {
                    ImportTarget = MovieFileImportTarget.EditionSlot;
                }
            }
        }
        public string SceneName { get; set; }
        public bool OtherVideoFiles { get; set; }
        public List<CustomFormat> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
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
