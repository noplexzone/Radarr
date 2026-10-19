using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class RenameMovieFileServiceEditionFixture : CoreTest<RenameMovieFileService>
    {
        private Movie _movie;
        private MovieFile _theatrical;
        private MovieFile _finalCut;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.Path = @"C:\Movies\Blade Runner (1982)".AsOsAgnostic())
                .Build();

            _theatrical = new MovieFile { Id = 1, MovieId = 1, RelativePath = "Blade Runner (1982).mkv" };
            _finalCut = new MovieFile { Id = 2, MovieId = 1, MovieEditionId = 2, Edition = "Final Cut", RelativePath = "Blade Runner (1982) {edition-Final Cut}.mkv" };

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _movie });

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<MovieFile> { _theatrical, _finalCut });

            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFilePath(_movie, It.IsAny<string>(), It.IsAny<string>()))
                .Returns<Movie, string, string>((m, name, ext) => System.IO.Path.Combine(m.Path, name + ext));
        }

        private void GivenNamingWithoutEdition()
        {
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(_movie, It.IsAny<MovieFile>(), null, null))
                .Returns("Blade Runner (1982) Bluray-1080p");
        }

        private void GivenNamingWithEdition()
        {
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(_movie, It.IsAny<MovieFile>(), null, null))
                .Returns<Movie, MovieFile, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((m, f, n, c) => f.Edition == null ? "Blade Runner (1982)" : $"Blade Runner (1982) {{edition-{f.Edition}}} Bluray-1080p");
        }

        [Test]
        public void should_not_rename_files_that_would_get_the_same_name()
        {
            GivenNamingWithoutEdition();

            Subject.Execute(new RenameMovieCommand { MovieIds = new List<int> { _movie.Id } });

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(It.IsAny<MovieFile>(), _movie), Times.Never());
            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void should_not_rename_file_onto_another_files_name()
        {
            // Final Cut would become the Theatrical file's current name
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(_movie, It.IsAny<MovieFile>(), null, null))
                .Returns("Blade Runner (1982)");

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<MovieFile> { _finalCut });

            Subject.Execute(new RenameFilesCommand { MovieId = _movie.Id, Files = new List<int> { _finalCut.Id } });

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(It.IsAny<MovieFile>(), _movie), Times.Never());
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_rename_files_when_names_include_edition()
        {
            GivenNamingWithEdition();

            Subject.Execute(new RenameMovieCommand { MovieIds = new List<int> { _movie.Id } });

            Mocker.GetMock<IMoveMovieFiles>().Verify(v => v.MoveMovieFile(_finalCut, _movie), Times.Once());
        }

        [Test]
        public void should_not_preview_colliding_renames()
        {
            GivenNamingWithoutEdition();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<MovieFile> { _theatrical, _finalCut });

            Subject.GetRenamePreviews(new List<int> { _movie.Id }).Should().BeEmpty();
        }
    }
}
