using System.Collections.Generic;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class DiskScanEditionTargetFixture : CoreTest<DiskScanService>
    {
        private Movie _movie;
        private string _discoveredPath;
        private List<ImportDecision> _decisions;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie { Id = 10, Title = "Test Movie", Path = "/movies/test" };
            _discoveredPath = "/movies/test/discovered.mkv";
            _decisions = new List<ImportDecision>
            {
                new ImportDecision(new LocalMovie { Movie = _movie, Path = _discoveredPath })
            };

            Mocker.GetMock<IRootFolderService>()
                .Setup(s => s.GetBestRootFolderPath(_movie.Path, null))
                .Returns("/movies");
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFiles(_movie.Path, true)).Returns(new[] { _discoveredPath });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(It.IsAny<string>())).Returns(100L);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.DeleteEmptyFolders).Returns(false);
            Mocker.GetMock<IMakeImportDecision>()
                .Setup(s => s.GetImportDecisions(It.IsAny<List<string>>(), _movie, false))
                .Returns(_decisions);
            Mocker.GetMock<IImportApprovedMovie>()
                .Setup(s => s.Import(It.IsAny<List<ImportDecision>>(), false, null, ImportMode.Auto))
                .Returns(new List<ImportResult>());
            Mocker.GetMock<IUpdateMediaInfo>().Setup(s => s.Update(It.IsAny<MovieFile>(), _movie)).Returns(false);
        }

        [TestCase(0, MovieFileImportTarget.Main)]
        [TestCase(5, MovieFileImportTarget.Unassigned)]
        public void should_make_rescan_discoveries_unassigned_only_when_a_main_file_already_exists(int mainFileId, MovieFileImportTarget expected)
        {
            _movie.MovieFileId = mainFileId;
            var existingFiles = mainFileId > 0
                ? new List<MovieFile>
                {
                    new MovieFile { Id = mainFileId, MovieId = _movie.Id, RelativePath = "main.mkv", Size = 100 }
                }
                : new List<MovieFile>();
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(_movie.Id)).Returns(existingFiles);

            Subject.Scan(_movie);

            Assert.That(_decisions[0].LocalMovie.ImportTarget, Is.EqualTo(expected));
        }

        [Test]
        public void should_treat_a_discovery_as_main_when_the_stored_main_pointer_is_stale()
        {
            _movie.MovieFileId = 5;
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<MovieFile>());

            Subject.Scan(_movie);

            Assert.That(_decisions[0].LocalMovie.ImportTarget, Is.EqualTo(MovieFileImportTarget.Main));
        }

        [Test]
        public void should_preserve_an_explicit_durable_slot_target_during_rescan()
        {
            _movie.MovieFileId = 5;
            _decisions[0].LocalMovie.MovieEditionSlotId = 42;
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<MovieFile>
                {
                    new MovieFile { Id = 5, MovieId = _movie.Id, RelativePath = "main.mkv", Size = 100 }
                });

            Subject.Scan(_movie);

            Assert.That(_decisions[0].LocalMovie.ImportTarget, Is.EqualTo(MovieFileImportTarget.EditionSlot));
            Assert.That(_decisions[0].LocalMovie.MovieEditionSlotId, Is.EqualTo(42));
        }
    }
}
