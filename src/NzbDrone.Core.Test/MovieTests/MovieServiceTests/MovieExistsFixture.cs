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
        private Movie _existingNoEdition;
        private Movie _existingExtendedCut;

        [SetUp]
        public void Setup()
        {
            _existingNoEdition = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .With(m => m.MovieEdition = "")
                .Build();

            _existingExtendedCut = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .With(m => m.MovieEdition = "Extended Cut")
                .Build();

            // Default: TmdbId 100 has one edition (no edition / blank)
            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(100))
                .Returns(new List<Movie> { _existingNoEdition });

            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(It.Is<int>(id => id != 100)))
                .Returns(new List<Movie>());

            // ImdbId fallback (only exercised when TmdbId == 0)
            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindByImdbId("tt0001234"))
                .Returns(_existingNoEdition);

            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindByImdbId(It.Is<string>(s => s != "tt0001234")))
                .Returns((Movie)null);
        }

        // ----- TmdbId present: edition-aware identity -----

        [Test]
        public void should_return_true_when_same_tmdb_and_same_edition_both_empty()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = "")
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();
        }

        [Test]
        public void should_return_true_when_same_tmdb_and_edition_null_matches_empty()
        {
            // null edition is treated as "" (no edition) and matches existing blank edition
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = null)
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();
        }

        [Test]
        public void should_return_false_when_same_tmdb_but_different_edition()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = "Extended Cut")
                .Build();

            Subject.MovieExists(movie).Should().BeFalse();
        }

        [Test]
        public void should_be_case_insensitive_when_comparing_editions()
        {
            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(100))
                .Returns(new List<Movie> { _existingExtendedCut });

            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = "extended cut")
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();
        }

        [Test]
        public void should_allow_third_edition_when_two_already_exist()
        {
            Mocker.GetMock<IMovieRepository>()
                .Setup(r => r.FindAllByTmdbId(100))
                .Returns(new List<Movie> { _existingNoEdition, _existingExtendedCut });

            var incomingThirdEdition = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = "Director's Cut")
                .Build();

            Subject.MovieExists(incomingThirdEdition).Should().BeFalse();
        }

        // ----- Constraint: ImdbId must NOT block a different edition -----

        [Test]
        public void should_not_check_imdbid_when_tmdbid_is_present()
        {
            // Same TMDB movie, same ImdbId, but a different edition — must NOT be blocked.
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .With(m => m.MovieEdition = "Extended Cut")
                .Build();

            Subject.MovieExists(movie).Should().BeFalse();

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByImdbId(It.IsAny<string>()), Times.Never());
        }

        // ----- TmdbId absent: fall back to ImdbId -----

        [Test]
        public void should_fall_back_to_imdbid_when_tmdbid_is_zero()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 0)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt0001234")
                .With(m => m.MovieMetadata.Value.Title = null)
                .With(m => m.MovieEdition = "")
                .Build();

            Subject.MovieExists(movie).Should().BeTrue();

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByImdbId("tt0001234"), Times.Once());
        }

        [Test]
        public void should_return_false_when_tmdbid_zero_and_no_imdbid_match()
        {
            // No title set so title fallback is skipped (avoids FindByTitles mock complexity).
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 0)
                .With(m => m.MovieMetadata.Value.ImdbId = "tt9999999")
                .With(m => m.MovieMetadata.Value.Title = null)
                .With(m => m.MovieEdition = "")
                .Build();

            Subject.MovieExists(movie).Should().BeFalse();
        }

        // ----- FindAllByTmdbId is exercised, not FindByTmdbId -----

        [Test]
        public void should_use_find_all_by_tmdb_id_not_single_overload()
        {
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.MovieMetadata.Value.TmdbId = 100)
                .With(m => m.MovieEdition = "Extended Cut")
                .Build();

            Subject.MovieExists(movie);

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindAllByTmdbId(100), Times.Once());

            Mocker.GetMock<IMovieRepository>()
                .Verify(r => r.FindByTmdbId(It.IsAny<int>()), Times.Never());
        }
    }
}
