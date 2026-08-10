using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileRepositoryFixture : DbTest<MediaFileRepository, MovieFile>
    {
        private Movie _movie1;
        private Movie _movie2;

        [SetUp]
        public void Setup()
        {
            _movie1 = Builder<Movie>.CreateNew()
                                    .With(s => s.Id = 7)
                                    .Build();

            _movie2 = Builder<Movie>.CreateNew()
                                    .With(s => s.Id = 8)
                                    .Build();
        }

        [Test]
        public void get_files_by_movie()
        {
            var files = Builder<MovieFile>.CreateListOfSize(10)
                .All()
                .With(c => c.Id = 0)
                .With(c => c.Quality = new QualityModel())
                .With(c => c.Languages = new List<Language> { Language.English })
                .Random(4)
                .With(s => s.MovieId = 12)
                .BuildListOfNew();

            Db.InsertMany(files);

            var movieFiles = Subject.GetFilesByMovie(12);

            movieFiles.Should().HaveCount(4);
            movieFiles.Should().OnlyContain(c => c.MovieId == 12);
        }


        [Test]
        public void should_find_files_by_durable_edition_slot_id()
        {
            var files = Builder<MovieFile>.CreateListOfSize(3)
                .All()
                .With(file => file.Id = 0)
                .With(file => file.MovieId = _movie1.Id)
                .With(file => file.MovieEditionSlotId = null)
                .With(file => file.Quality = new QualityModel())
                .With(file => file.Languages = new List<Language> { Language.English })
                .TheFirst(1)
                .With(file => file.MovieEditionSlotId = 41)
                .TheNext(1)
                .With(file => file.MovieEditionSlotId = 42)
                .BuildListOfNew();

            Db.InsertMany(files);

            Subject.FindByEditionSlotId(41).Id.Should().Be(files[0].Id);
            Subject.GetFilesByEditionSlotIds(new[] { 42 }).Should().ContainSingle(file => file.Id == files[1].Id);
        }

        [Test]
        public void should_return_only_non_main_unassigned_files()
        {
            var movie = Builder<Movie>.CreateNew().With(item => item.MovieFileId = 0).BuildNew();
            Db.Insert(movie);
            var files = Builder<MovieFile>.CreateListOfSize(3)
                .All()
                .With(file => file.Id = 0)
                .With(file => file.MovieId = movie.Id)
                .With(file => file.MovieEditionSlotId = null)
                .With(file => file.Quality = new QualityModel())
                .With(file => file.Languages = new List<Language> { Language.English })
                .TheLast(1)
                .With(file => file.MovieEditionSlotId = 77)
                .BuildListOfNew();
            Db.InsertMany(files);
            movie.MovieFileId = files[0].Id;
            Db.Update(movie);

            Subject.GetUnassignedFiles(movie.Id).Should().ContainSingle(file => file.Id == files[1].Id);
        }

        [Test]
        public void should_delete_files_by_movieId()
        {
            var items = Builder<MovieFile>.CreateListOfSize(5)
                .TheFirst(1)
                .With(c => c.MovieId = _movie2.Id)
                .TheRest()
                .With(c => c.MovieId = _movie1.Id)
                .All()
                .With(c => c.Id = 0)
                .With(c => c.Quality = new QualityModel(Quality.Bluray1080p))
                .With(c => c.Languages = new List<Language> { Language.English })
                .BuildListOfNew();

            Db.InsertMany(items);

            Subject.DeleteForMovies(new List<int> { _movie1.Id });

            var removedItems = Subject.GetFilesByMovie(_movie1.Id);
            var nonRemovedItems = Subject.GetFilesByMovie(_movie2.Id);

            removedItems.Should().HaveCount(0);
            nonRemovedItems.Should().HaveCount(1);
        }
    }
}
