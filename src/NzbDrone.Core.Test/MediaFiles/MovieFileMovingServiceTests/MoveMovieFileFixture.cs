using System;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieFileMovingServiceTests
{
    [TestFixture]
    public class MoveMovieFileFixture : CoreTest<MovieFileMovingService>
    {
        private Movie _movie;
        private MovieFile _movieFile;
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                     .With(s => s.Path = @"C:\Test\Movies\Movie".AsOsAgnostic())
                                     .Build();

            _movieFile = Builder<MovieFile>.CreateNew()
                                               .With(f => f.Path = null)
                                               .With(f => f.RelativePath = @"File.avi")
                                               .Build();

            _localMovie = Builder<LocalMovie>.CreateNew()
                                                 .With(l => l.Movie = _movie)
                                                 .Build();

            Mocker.GetMock<IBuildFileNames>()
                  .Setup(s => s.BuildFileName(It.IsAny<Movie>(), It.IsAny<MovieFile>(), null, null))
                  .Returns("File Name");

            Mocker.GetMock<IBuildFileNames>()
                  .Setup(s => s.BuildFilePath(It.IsAny<Movie>(), It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(@"C:\Test\Movies\Movie\File Name.avi".AsOsAgnostic());

            var rootFolder = @"C:\Test\Movies\".AsOsAgnostic();

            Mocker.GetMock<IRootFolderService>()
                .Setup(s => s.GetBestRootFolderPath(It.IsAny<string>(), null))
                .Returns(rootFolder);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(rootFolder))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);
        }

        [Test]
        public void should_catch_UnauthorizedAccessException_during_folder_inheritance()
        {
            WindowsOnly();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.InheritFolderPermissions(It.IsAny<string>()))
                  .Throws<UnauthorizedAccessException>();

            Subject.MoveMovieFile(_movieFile, _localMovie);
        }

        [Test]
        public void should_catch_InvalidOperationException_during_folder_inheritance()
        {
            WindowsOnly();

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.InheritFolderPermissions(It.IsAny<string>()))
                  .Throws<InvalidOperationException>();

            Subject.MoveMovieFile(_movieFile, _localMovie);
        }

        [Test]
        public void should_notify_on_movie_folder_creation()
        {
            Subject.MoveMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(s => s.PublishEvent<MovieFolderCreatedEvent>(It.Is<MovieFolderCreatedEvent>(p =>
                      p.MovieFolder.IsNotNullOrWhiteSpace())), Times.Once());
        }

        [Test]
        public void should_not_notify_if_movie_folder_already_exists()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderExists(_movie.Path))
                  .Returns(true);

            Subject.MoveMovieFile(_movieFile, _localMovie);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(s => s.PublishEvent<MovieFolderCreatedEvent>(It.Is<MovieFolderCreatedEvent>(p =>
                      p.MovieFolder.IsNotNullOrWhiteSpace())), Times.Never());
        }

        [Test]
        public void should_preflight_the_exact_destination_without_creating_folders()
        {
            var destination = @"C:\Test\Movies\Movie\File Name.avi".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(destination)).Returns(false);

            Subject.PreflightMovieFile(_movieFile, _localMovie, null).Should().Be(destination);

            Mocker.GetMock<IDiskProvider>().Verify(s => s.CreateFolder(It.IsAny<string>()), Times.Never);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_reject_a_destination_collision_during_preflight()
        {
            var destination = @"C:\Test\Movies\Movie\File Name.avi".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(destination)).Returns(true);

            Assert.Throws<DestinationAlreadyExistsException>(() => Subject.PreflightMovieFile(_movieFile, _localMovie, null));

            Mocker.GetMock<IDiskProvider>().Verify(s => s.CreateFolder(It.IsAny<string>()), Times.Never);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }

        [Test]
        public void should_allow_the_exact_replaced_file_as_the_preflight_destination()
        {
            var destination = @"C:\Test\Movies\Movie\File Name.avi".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(destination)).Returns(true);

            Subject.PreflightMovieFile(_movieFile, _localMovie, destination).Should().Be(destination);
        }

        [Test]
        public void should_reject_a_missing_source_during_preflight()
        {
            var source = Path.Combine(_movie.Path, _movieFile.RelativePath);
            var destination = @"C:\Test\Movies\Movie\File Name.avi".AsOsAgnostic();
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(source)).Returns(false);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(destination)).Returns(false);

            Assert.Throws<FileNotFoundException>(() => Subject.PreflightMovieFile(_movieFile, _localMovie, null));

            Mocker.GetMock<IDiskProvider>().Verify(s => s.CreateFolder(It.IsAny<string>()), Times.Never);
            Mocker.GetMock<IDiskTransferService>().VerifyNoOtherCalls();
        }
    }
}
