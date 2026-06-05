using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]

    public class QualityAllowedByProfileSpecificationFixture : CoreTest<QualityAllowedByProfileSpecification>
    {
        private RemoteMovie _remoteMovie;

        public static object[] AllowedTestCases =
        {
            new object[] { Quality.DVD },
            new object[] { Quality.HDTV720p },
            new object[] { Quality.Bluray1080p }
        };

        public static object[] DeniedTestCases =
        {
            new object[] { Quality.SDTV },
            new object[] { Quality.WEBDL720p },
            new object[] { Quality.Bluray720p }
        };

        [SetUp]
        public void Setup()
        {
            var fakeSeries = Builder<Movie>.CreateNew()
                .With(c => c.QualityProfile = new QualityProfile { Cutoff = Quality.Bluray1080p.Id })
                         .Build();

            _remoteMovie = new RemoteMovie
            {
                Movie = fakeSeries,
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.DVD, new Revision(version: 2)) },
            };
        }

        [Test]
        [TestCaseSource("AllowedTestCases")]
        public void should_allow_if_quality_is_defined_in_profile(Quality qualityType)
        {
            _remoteMovie.ParsedMovieInfo.Quality.Quality = qualityType;
            _remoteMovie.Movie.QualityProfile.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD, Quality.HDTV720p, Quality.Bluray1080p);

            Subject.IsSatisfiedBy(_remoteMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        [TestCaseSource("DeniedTestCases")]
        public void should_not_allow_if_quality_is_not_defined_in_profile(Quality qualityType)
        {
            _remoteMovie.ParsedMovieInfo.Quality.Quality = qualityType;
            _remoteMovie.Movie.QualityProfile.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD, Quality.HDTV720p, Quality.Bluray1080p);

            Subject.IsSatisfiedBy(_remoteMovie, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void slot_override_profile_allows_quality_rejected_by_movie_profile()
        {
            // Movie profile only allows DVD; override allows Bluray
            _remoteMovie.ParsedMovieInfo.Quality.Quality = Quality.Bluray1080p;
            _remoteMovie.Movie.QualityProfile.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD);

            var overrideProfile = new QualityProfile
            {
                Cutoff = Quality.Bluray1080p.Id,
                Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD, Quality.Bluray1080p)
            };

            var criteria = new MovieSearchCriteria
            {
                MovieEditionSlotId = 1,
                OverrideQualityProfile = overrideProfile
            };

            Subject.IsSatisfiedBy(_remoteMovie, criteria).Accepted.Should().BeTrue();
        }

        [Test]
        public void slot_override_profile_rejects_quality_allowed_by_movie_profile()
        {
            // Movie profile allows Bluray; override profile only allows DVD
            _remoteMovie.ParsedMovieInfo.Quality.Quality = Quality.Bluray1080p;
            _remoteMovie.Movie.QualityProfile.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD, Quality.Bluray1080p);

            var overrideProfile = new QualityProfile
            {
                Cutoff = Quality.DVD.Id,
                Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD)
            };

            var criteria = new MovieSearchCriteria
            {
                MovieEditionSlotId = 1,
                OverrideQualityProfile = overrideProfile
            };

            Subject.IsSatisfiedBy(_remoteMovie, criteria).Accepted.Should().BeFalse();
        }

        [Test]
        public void normal_search_without_override_uses_movie_profile()
        {
            // Normal MovieSearchCriteria without override — must still use movie profile
            _remoteMovie.ParsedMovieInfo.Quality.Quality = Quality.Bluray1080p;
            _remoteMovie.Movie.QualityProfile.Items = Qualities.QualityFixture.GetDefaultQualities(Quality.DVD, Quality.Bluray1080p);

            var criteria = new MovieSearchCriteria { MovieEditionSlotId = null };

            Subject.IsSatisfiedBy(_remoteMovie, criteria).Accepted.Should().BeTrue();
        }
    }
}
