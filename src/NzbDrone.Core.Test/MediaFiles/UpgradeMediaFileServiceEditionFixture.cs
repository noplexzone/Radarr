using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class UpgradeMediaFileServiceEditionFixture : CoreTest<UpgradeMediaFileService>
    {
        private MovieFile _newFile;
        private MovieFile _theatricalFile;
        private MovieFile _finalCutFile;
        private MovieEdition _finalCut;
        private LocalMovie _localMovie;
        private string _finalCutPath;
        private string _backupPath;

        [SetUp]
        public void Setup()
        {
            var movie = new Movie { Id = 1, Path = @"C:\Test\Movies\Blade Runner (1982)".AsOsAgnostic() };

            _theatricalFile = new MovieFile { Id = 1, MovieId = 1, RelativePath = "Blade Runner (1982).mkv" };
            _finalCutFile = new MovieFile { Id = 2, MovieId = 1, MovieEditionId = 2, RelativePath = "Blade Runner (1982) {edition-Final Cut}.mkv" };

            movie.MovieFileId = _theatricalFile.Id;
            movie.MovieFile = _theatricalFile;

            _finalCut = MovieEdition.CreateNew(movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            _localMovie = new LocalMovie { Movie = movie, MovieEdition = _finalCut };
            _newFile = Builder<MovieFile>.CreateNew().Build();

            _finalCutPath = Path.Combine(movie.Path, _finalCutFile.RelativePath);
            _backupPath = _finalCutPath + ".radarr-upgrade";

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.FolderExists(Directory.GetParent(movie.Path).FullName))
                .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.GetParentFolder(It.IsAny<string>()))
                .Returns<string>(c => Path.GetDirectoryName(c));

            // Simulate the disk: the edition's file moves aside before the new file is put in place
            var finalCutOnDisk = true;
            var backupOnDisk = false;

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.FileExists(It.IsAny<string>()))
                .Returns<string>(p => p == _finalCutPath ? finalCutOnDisk : p == _backupPath ? backupOnDisk : true);

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.MoveFile(_finalCutPath, _backupPath, It.IsAny<bool>()))
                .Callback(() =>
                {
                    finalCutOnDisk = false;
                    backupOnDisk = true;
                });

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.MoveFile(_backupPath, _finalCutPath, It.IsAny<bool>()))
                .Callback(() =>
                {
                    finalCutOnDisk = true;
                    backupOnDisk = false;
                });
        }

        private void GivenFinalCutHasFile()
        {
            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetCurrentFile(_localMovie.Movie, _finalCut))
                .Returns(_finalCutFile);
        }

        [Test]
        public void should_not_touch_other_edition_when_edition_is_empty()
        {
            var result = Subject.UpgradeMovieFile(_newFile, _localMovie);

            result.OldFiles.Should().BeEmpty();
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_replace_only_the_edition_file()
        {
            GivenFinalCutHasFile();

            var result = Subject.UpgradeMovieFile(_newFile, _localMovie);

            result.OldFiles.Should().ContainSingle().Which.MovieFile.Should().Be(_finalCutFile);
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_finalCutFile, DeleteMediaFileReason.Upgrade), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_theatricalFile, It.IsAny<DeleteMediaFileReason>()), Times.Never());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_finalCutPath, It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_move_existing_file_aside_before_importing()
        {
            GivenFinalCutHasFile();

            Mocker.GetMock<IMoveMovieFiles>()
                .Setup(s => s.MoveMovieFile(_newFile, _localMovie))
                .Callback(() => Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never()))
                .Returns(_newFile);

            Subject.UpgradeMovieFile(_newFile, _localMovie);

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(_finalCutPath, _backupPath, true), Times.Once());
        }

        [Test]
        public void should_restore_existing_file_when_import_fails()
        {
            GivenFinalCutHasFile();

            Mocker.GetMock<IMoveMovieFiles>()
                .Setup(s => s.MoveMovieFile(_newFile, _localMovie))
                .Throws(new IOException("Disk full"));

            Assert.Throws<IOException>(() => Subject.UpgradeMovieFile(_newFile, _localMovie));

            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(_backupPath, _finalCutPath, It.IsAny<bool>()), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(It.IsAny<MovieFile>(), It.IsAny<DeleteMediaFileReason>()), Times.Never());
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            _localMovie.OldFiles.Should().BeEmpty();
        }

        [Test]
        public void should_recycle_backup_when_new_file_took_the_old_name()
        {
            GivenFinalCutHasFile();

            Mocker.GetMock<IMoveMovieFiles>()
                .Setup(s => s.MoveMovieFile(_newFile, _localMovie))
                .Callback(() => Mocker.GetMock<IDiskProvider>()
                    .Setup(c => c.FileExists(_finalCutPath))
                    .Returns(true))
                .Returns(_newFile);

            Subject.UpgradeMovieFile(_newFile, _localMovie);

            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(_backupPath, It.IsAny<string>()), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(v => v.MoveFile(_backupPath, _finalCutPath, It.IsAny<bool>()), Times.Never());
        }
    }
}
