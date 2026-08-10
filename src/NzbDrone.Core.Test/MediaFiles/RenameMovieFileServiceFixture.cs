using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class RenameMovieFileServiceFixture : CoreTest<RenameMovieFileService>
    {
        private Movie _movie;
        private List<MovieFile> _movieFiles;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                     .Build();

            _movieFiles = Builder<MovieFile>.CreateListOfSize(2)
                                                .All()
                                                .With(e => e.MovieId = _movie.Id)
                                                .Build()
                                                .ToList();

            Mocker.GetMock<IMovieService>()
                  .Setup(s => s.GetMovie(_movie.Id))
                  .Returns(_movie);
        }

        private void GivenNoMovieFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<MovieFile>());
        }

        private void GivenMovieFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(_movieFiles);
        }

        private void GivenMovedFiles()
        {
            Mocker.GetMock<IMoveMovieFiles>()
                  .Setup(s => s.MoveMovieFile(It.IsAny<MovieFile>(), _movie));
        }

        [Test]
        public void should_not_publish_event_if_no_files_to_rename()
        {
            GivenNoMovieFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_not_publish_event_if_no_files_are_renamed()
        {
            GivenMovieFiles();

            Mocker.GetMock<IMoveMovieFiles>()
                  .Setup(s => s.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()))
                  .Throws(new SameFilenameException("Same file name", "Filename"));

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_publish_event_if_files_are_renamed()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<MovieRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_update_moved_files()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_movie.Id, new List<int> { 1 }));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Update(It.IsAny<MovieFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_get_moviefiles_by_ids_only()
        {
            GivenMovieFiles();
            GivenMovedFiles();

            var files = new List<int> { 1 };

            Subject.Execute(new RenameFilesCommand(_movie.Id, files));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.GetMovies(files), Times.Once());
        }

        [Test]
        public void should_preview_main_slot_and_unassigned_files_with_distinct_destinations()
        {
            _movie.Path = "/movies/test";
            _movie.MovieFileId = 1;
            _movieFiles = new List<MovieFile>
            {
                new MovieFile { Id = 1, MovieId = _movie.Id, RelativePath = "main-old.mkv" },
                new MovieFile { Id = 2, MovieId = _movie.Id, MovieEditionSlotId = 42, RelativePath = "slot-old.mkv" },
                new MovieFile { Id = 3, MovieId = _movie.Id, RelativePath = "unassigned-old.mkv" }
            };

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _movie });
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(_movieFiles);
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(_movie, It.IsAny<MovieFile>(), null, null))
                .Returns<Movie, MovieFile, NamingConfig, List<NzbDrone.Core.CustomFormats.CustomFormat>>((_, file, _, _) => $"new-{file.Id}");
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFilePath(_movie, It.IsAny<string>(), ".mkv"))
                .Returns<Movie, string, string>((_, name, extension) => $"/movies/test/{name}{extension}");

            var previews = Subject.GetRenamePreviews(new List<int> { _movie.Id });

            Assert.That(previews.Select(p => p.MovieFileId), Is.EquivalentTo(new[] { 1, 2, 3 }));
            Assert.That(previews.Select(p => p.NewPath).Distinct().Count(), Is.EqualTo(3));
            Assert.That(_movieFiles.Single(f => f.Id == 2).MovieEditionSlotId, Is.EqualTo(42));
        }

        [Test]
        public void should_rename_main_slot_and_unassigned_files_without_changing_slot_assignment()
        {
            _movie.MovieFileId = 1;
            _movieFiles = new List<MovieFile>
            {
                new MovieFile { Id = 1, MovieId = _movie.Id, RelativePath = "main.mkv" },
                new MovieFile { Id = 2, MovieId = _movie.Id, MovieEditionSlotId = 42, RelativePath = "slot.mkv" },
                new MovieFile { Id = 3, MovieId = _movie.Id, RelativePath = "unassigned.mkv" }
            };
            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _movie });
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(_movieFiles);
            GivenMovedFiles();

            Subject.Execute(new RenameMovieCommand { MovieIds = new List<int> { _movie.Id } });

            foreach (var file in _movieFiles)
            {
                Mocker.GetMock<IMoveMovieFiles>()
                    .Verify(s => s.MoveMovieFile(file, _movie), Times.Once());
            }

            Assert.That(_movieFiles.Single(f => f.Id == 2).MovieEditionSlotId, Is.EqualTo(42));
        }

        [Test]
        public void should_validate_every_movie_in_a_bulk_command_before_moving_any_file()
        {
            var firstMovie = new Movie { Id = 10, Title = "First" };
            var secondMovie = new Movie { Id = 20, Title = "Second" };
            var firstFile = new MovieFile { Id = 100, MovieId = firstMovie.Id, RelativePath = "first.mkv" };
            var invalidSecondFile = new MovieFile { Id = 200, MovieId = secondMovie.Id, MovieEditionSlotId = 42, RelativePath = "second.mkv" };

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { firstMovie, secondMovie });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(firstMovie.Id)).Returns(new List<MovieFile> { firstFile });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesByMovie(secondMovie.Id)).Returns(new List<MovieFile> { invalidSecondFile });
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(secondMovie, invalidSecondFile, null, null))
                .Throws(new NamingFormatException("Missing edition slot"));

            Assert.Throws<NamingFormatException>(() => Subject.Execute(new RenameMovieCommand
            {
                MovieIds = new List<int> { firstMovie.Id, secondMovie.Id }
            }));

            Mocker.GetMock<IMoveMovieFiles>()
                .Verify(s => s.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()), Times.Never());
        }

        [Test]
        public void should_validate_all_names_before_moving_any_file()
        {
            GivenMovieFiles();
            GivenMovedFiles();
            Mocker.GetMock<IBuildFileNames>()
                .Setup(s => s.BuildFileName(_movie, _movieFiles[1], null, null))
                .Throws(new NamingFormatException("Missing edition slot"));

            Assert.Throws<NamingFormatException>(() => Subject.Execute(new RenameFilesCommand(_movie.Id, _movieFiles.Select(f => f.Id).ToList())));

            Mocker.GetMock<IMoveMovieFiles>()
                .Verify(s => s.MoveMovieFile(It.IsAny<MovieFile>(), It.IsAny<Movie>()), Times.Never());
        }
    }
}
