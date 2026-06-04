using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.MovieServiceTests
{
    [TestFixture]
    public class MovieExistsFixture : CoreTest<MovieService>
    {
        private Movie _existingMovie;

        [SetUp]
        public void Setup()
        {
            _existingMovie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .Build();

            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(100))
                .Returns(new List<Movie> { _existingMovie });

            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(It.Is<int>(id => id != 100)))
                .Returns(new List<Movie>());

            // ImdbId fallback (only exercised when TmdbId == 0)
            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindByImdbId("tt0001234"))
                .Returns(_existingMovie);

            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindByImdbId(It.Is<string>(s => s != "tt0001234")))
                .Returns((Movie)null);
        }

        // ----- TmdbId present -----

        [Test]
        public void should_return_true_when_movie_with_same_tmdb_exists()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();
        }

        [Test]
        public void should_return_false_when_no_movie_with_tmdb_id_exists()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 999)
                .Build();

            Subject.MovieExists(movie).Should().BeFalse();
        }

        [Test]
        public void should_not_check_imdbid_when_tmdbid_is_present()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .Build();

            Subject.MovieExists(movie);

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByImdbId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_use_find_all_by_tmdb_id_not_single_overload()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .Build();

            Subject.MovieExists(movie);

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindAllByTmdbId(100), Times.Once());

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByTmdbId(It.IsAny<int>()), Times.Never());
        }

        // ----- TmdbId absent: fall back to ImdbId -----

        [Test]
        public void should_fall_back_to_imdbid_when_tmdbid_is_zero()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 0)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .With(m => m.MovieMetadata.Value.Title = null)
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByImdbId("tt0001234"), Times.Once());
        }

        [Test]
        public void should_return_false_when_tmdbid_zero_and_no_imdbid_match()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 0)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt9999999")
                .With(m => m.MovieMetadata.Value.Title = null)
                .Build();

            Subject.MovieExists(movie).Should().BeFalse();
        }
    }
}
