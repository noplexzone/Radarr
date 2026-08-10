using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                  .With(f => f.MovieId = _localMovie.Movie.Id)
                  .With(f => f.ImportTarget = _localMovie.ImportTarget)
                  .With(f => f.MovieEditionSlotId = null)
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
                    MovieId = _localMovie.Movie.Id,
                    RelativePath = @"A.Movie.2019.avi",
                };

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetMovie(_localMovie.Movie.MovieFileId))
                .Returns(_localMovie.Movie.MovieFile);
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

        // --- Durable edition-slot-aware upgrade tests ---

        [Test]
        public void should_replace_only_the_file_assigned_to_the_explicit_slot()
        {
            _localMovie.MovieEditionSlotId = 7;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 7;
            var slot = new MovieEditionSlot { Id = 7, MovieId = _localMovie.Movie.Id, EditionName = "IMAX" };
            var slotFile = new MovieFile { Id = 10, MovieId = _localMovie.Movie.Id, MovieEditionSlotId = 7, RelativePath = "IMAX.mkv" };

            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(slot.Id)).Returns(slot);
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(slot.Id))
                .Returns(slotFile);

            var result = Subject.UpgradeMovieFile(_movieFile, _localMovie);

            result.OldFiles.Should().ContainSingle(f => f.MovieFile.Id == slotFile.Id);
        }

        [Test]
        public void should_not_replace_a_file_from_a_different_slot()
        {
            _localMovie.MovieEditionSlotId = 7;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 7;
            var slot = new MovieEditionSlot { Id = 7, MovieId = _localMovie.Movie.Id, EditionName = "IMAX" };
            var otherSlotFile = new MovieFile { Id = 20, MovieId = _localMovie.Movie.Id, MovieEditionSlotId = 8, RelativePath = "Other.mkv" };

            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(slot.Id)).Returns(slot);
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(slot.Id))
                .Returns((MovieFile)null);

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>()
                .Verify(s => s.Delete(otherSlotFile, It.IsAny<DeleteMediaFileReason>()), Times.Never);
        }

        [Test]
        public void should_reject_a_missing_explicit_slot_before_any_mutation()
        {
            _localMovie.MovieEditionSlotId = 99;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 99;
            Mocker.GetMock<IMovieEditionSlotService>().Setup(s => s.GetById(99)).Returns((MovieEditionSlot)null);

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IRecycleBinProvider>().VerifyNoOtherCalls();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IMoveMovieFiles>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_reject_an_explicit_slot_owned_by_another_movie_before_any_mutation()
        {
            _localMovie.MovieEditionSlotId = 7;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 7;
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetById(7))
                .Returns(new MovieEditionSlot { Id = 7, MovieId = _localMovie.Movie.Id + 1 });

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IRecycleBinProvider>().VerifyNoOtherCalls();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IMoveMovieFiles>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_reject_an_edition_slot_target_without_a_slot_id_before_any_mutation()
        {
            _localMovie.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IRecycleBinProvider>().VerifyNoOtherCalls();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IMoveMovieFiles>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_not_replace_main_or_slot_files_for_an_unassigned_import()
        {
            GivenSingleMovieWithSingleMovieFile();
            _localMovie.ImportTarget = MovieFileImportTarget.Unassigned;
            _movieFile.ImportTarget = MovieFileImportTarget.Unassigned;

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMediaFileService>().Verify(s => s.GetMovie(It.IsAny<int>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.FindByEditionSlotId(It.IsAny<int>()), Times.Never);
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
        }

        [Test]
        public void parser_edition_on_main_target_should_replace_only_the_main_file_id()
        {
            GivenSingleMovieWithSingleMovieFile();
            _localMovie.Edition = "IMAX";

            var result = Subject.UpgradeMovieFile(_movieFile, _localMovie);

            result.OldFiles.Should().ContainSingle(f => f.MovieFile.Id == _localMovie.Movie.MovieFileId);
            Mocker.GetMock<IMovieEditionSlotService>().Verify(s => s.GetById(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void should_preflight_the_destination_before_deleting_the_existing_file()
        {
            GivenSingleMovieWithSingleMovieFile();
            Mocker.GetMock<IMoveMovieFiles>()
                .Setup(s => s.PreflightMovieFile(_movieFile, _localMovie, It.IsAny<string>()))
                .Throws(new DestinationAlreadyExistsException("collision"));

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IRecycleBinProvider>().VerifyNoOtherCalls();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IMoveMovieFiles>().Verify(s => s.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>()), Times.Never);
        }

        [Test]
        public void should_reject_a_cross_movie_file_attached_to_the_target_slot_before_mutation()
        {
            _localMovie.MovieEditionSlotId = 7;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 7;
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetById(7))
                .Returns(new MovieEditionSlot { Id = 7, MovieId = _localMovie.Movie.Id });
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.FindByEditionSlotId(7))
                .Returns(new MovieFile { Id = 20, MovieId = _localMovie.Movie.Id + 1, MovieEditionSlotId = 7 });

            Assert.Throws<InvalidOperationException>(() => Subject.UpgradeMovieFile(_movieFile, _localMovie));

            Mocker.GetMock<IRecycleBinProvider>().VerifyNoOtherCalls();
            Mocker.GetMock<IMediaFileService>().Verify(s => s.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never);
            Mocker.GetMock<IMoveMovieFiles>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_preserve_the_edition_slot_id_through_preflight_and_move()
        {
            _localMovie.MovieEditionSlotId = 7;
            _movieFile.ImportTarget = MovieFileImportTarget.EditionSlot;
            _movieFile.MovieEditionSlotId = 7;
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetById(7))
                .Returns(new MovieEditionSlot { Id = 7, MovieId = _localMovie.Movie.Id });

            Subject.UpgradeMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IMoveMovieFiles>()
                .Verify(s => s.PreflightMovieFile(It.Is<MovieFile>(f => f.MovieEditionSlotId == 7), _localMovie, null), Times.Once);
            Mocker.GetMock<IMoveMovieFiles>()
                .Verify(s => s.MoveMovieFile(It.Is<MovieFile>(f => f.MovieEditionSlotId == 7), _localMovie), Times.Once);
        }
    }
}
