using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class UpgradeMediaFileServiceFixture : CoreTest<UpgradeMediaFileService>
    {
        private MovieFile _movieFile;
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            _localMovie = new LocalMovie();
            _localMovie.Movie = new Movie
            {
                Path = @"C:\Test\Movies\Movie".AsOsAgnostic()
            };

            _movieFile = Builder<MovieFile>
                  .CreateNew()
                  .Build();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(c => c.FolderExists(Directory.GetParent(_localMovie.Movie.Path).FullName))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(c => c.GetParentFolder(It.IsAny<string>()))
                  .Returns<string>(c => Path.GetDirectoryName(c));

            // Default: no edition slots
            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(It.IsAny<int>()))
                  .Returns(new List<MovieEditionSlot>());
        }

        private void GivenSingleMovieWithSingleMovieFile()
        {
            _localMovie.Movie.MovieFileId = 1;
            _localMovie.Movie.MovieFile =
                new MovieFile
                {
                    Id = 1,
                    RelativePath = @"A.Movie.2019.avi",
                };
        }

        [Test]
        public void should_delete_single_movie_file_once()
        {
            GivenSingleMovieWithSingleMovieFile();

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_delete_movie_file_from_database()
        {
            GivenSingleMovieWithSingleMovieFile();

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(It.IsAny<MovieFile>(), DeleteMediaFileReason.Upgrade), Times.Once());
        }

        [Test]
        public void should_delete_existing_file_fromdb_if_file_doesnt_exist()
        {
            GivenSingleMovieWithSingleMovieFile();

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.FileExists(It.IsAny<string>()))
                .Returns(false);

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_localMovie.Movie.MovieFile, DeleteMediaFileReason.Upgrade), Times.Once());
        }

        [Test]
        public void should_not_try_to_recyclebin_existing_file_if_file_doesnt_exist()
        {
            GivenSingleMovieWithSingleMovieFile();

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.FileExists(It.IsAny<string>()))
                .Returns(false);

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_return_old_movie_file_in_oldFiles()
        {
            GivenSingleMovieWithSingleMovieFile();

            Subject.UpgradeMovieFile(_movieFile, _localMovie).OldFiles.Count.Should().Be(1);
        }

        [Test]
        public void should_throw_if_there_are_existing_movie_files_and_the_root_folder_is_missing()
        {
            GivenSingleMovieWithSingleMovieFile();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(c => c.FolderExists(Directory.GetParent(_localMovie.Movie.Path).FullName))
                  .Returns(false);

            Assert.Throws<RootFolderNotFoundException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_localMovie.Movie.MovieFile, DeleteMediaFileReason.Upgrade), Times.Never());
        }

        // --- Edition-slot-aware upgrade tests ---

        [Test]
        public void should_use_slot_file_as_existing_file_when_upgrading_edition()
        {
            // Movie.MovieFile points to IMAX (slot B), but we're upgrading Director's Cut (slot A)
            _localMovie.Movie.MovieFileId = 2;
            _localMovie.Movie.MovieFile = new MovieFile { Id = 2, RelativePath = "IMAX.mkv" };
            _localMovie.Edition = "Director's Cut";

            var slotA = new MovieEditionSlot { Id = 1, MovieId = _localMovie.Movie.Id, EditionName = "Director's Cut", MovieFileId = 1 };
            var slotAFile = new MovieFile { Id = 1, RelativePath = "DirectorsCut.mkv" };

            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(_localMovie.Movie.Id))
                  .Returns(new List<MovieEditionSlot> { slotA });

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovie(1))
                  .Returns(slotAFile);

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            // Only Director's Cut file should be deleted
            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.Is<MovieFile>(f => f.Id == 1), DeleteMediaFileReason.Upgrade), Times.Once);
        }

        [Test]
        public void should_not_delete_other_edition_slot_file_when_upgrading()
        {
            // Movie.MovieFile points to IMAX (slot B), we're upgrading Director's Cut (slot A)
            _localMovie.Movie.MovieFileId = 2;
            _localMovie.Movie.MovieFile = new MovieFile { Id = 2, RelativePath = "IMAX.mkv" };
            _localMovie.Edition = "Director's Cut";

            var slotA = new MovieEditionSlot { Id = 1, MovieId = _localMovie.Movie.Id, EditionName = "Director's Cut", MovieFileId = 1 };
            var slotAFile = new MovieFile { Id = 1, RelativePath = "DirectorsCut.mkv" };

            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(_localMovie.Movie.Id))
                  .Returns(new List<MovieEditionSlot> { slotA });

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovie(1))
                  .Returns(slotAFile);

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            // IMAX file (id=2) must not be touched
            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.Is<MovieFile>(f => f.Id == 2), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IRecycleBinProvider>()
                  .Verify(v => v.DeleteFile(It.Is<string>(p => p.Contains("IMAX")), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public void should_not_delete_any_file_when_edition_slot_has_no_existing_file()
        {
            // Slot exists but has no file yet (first import for this edition)
            _localMovie.Movie.MovieFileId = 1;
            _localMovie.Movie.MovieFile = new MovieFile { Id = 1, RelativePath = "Existing.mkv" };
            _localMovie.Edition = "Director's Cut";

            var slot = new MovieEditionSlot { Id = 1, MovieId = _localMovie.Movie.Id, EditionName = "Director's Cut", MovieFileId = null };

            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(_localMovie.Movie.Id))
                  .Returns(new List<MovieEditionSlot> { slot });

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
        }

        [Test]
        public void should_not_delete_any_file_when_no_matching_edition_slot_exists()
        {
            // Edition is specified but no slot matches — do not fall back to Movie.MovieFile
            _localMovie.Movie.MovieFileId = 1;
            _localMovie.Movie.MovieFile = new MovieFile { Id = 1, RelativePath = "Existing.mkv" };
            _localMovie.Edition = "Director's Cut";

            // No slots at all
            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(_localMovie.Movie.Id))
                  .Returns(new List<MovieEditionSlot>());

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
        }

        [Test]
        public void should_return_slot_file_in_oldFiles_when_upgrading_edition()
        {
            _localMovie.Edition = "Director's Cut";

            var slot = new MovieEditionSlot { Id = 1, MovieId = _localMovie.Movie.Id, EditionName = "Director's Cut", MovieFileId = 5 };
            var slotFile = new MovieFile { Id = 5, RelativePath = "DirectorsCut.mkv" };

            Mocker.GetMock<IMovieEditionSlotService>()
                  .Setup(s => s.GetForMovie(_localMovie.Movie.Id))
                  .Returns(new List<MovieEditionSlot> { slot });

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovie(5))
                  .Returns(slotFile);

            var result = Subject.UpgradeMovieFile(_movieFile, _localMovie);

            result.OldFiles.Should().ContainSingle(f => f.MovieFile.Id == 5);
        }
    }
}
