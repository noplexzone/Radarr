using Moq;
using NUnit.Framework;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class EditionNamingCheckFixture : CoreTest<EditionNamingCheck>
    {
        private NamingConfig _namingConfig;

        [SetUp]
        public void Setup()
        {
            _namingConfig = NamingConfig.Default;
            _namingConfig.RenameMovies = true;
            _namingConfig.StandardMovieFormat = "{Movie Title} ({Release Year}) {Quality Full}";

            Mocker.GetMock<INamingConfigService>()
                .Setup(s => s.GetConfig())
                .Returns(() => _namingConfig);

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.AnyMovieHasMultipleEditions())
                .Returns(true);

            Mocker.GetMock<ILocalizationService>()
                .Setup(s => s.GetLocalizedString(It.IsAny<string>()))
                .Returns("Edition warning");
        }

        [Test]
        public void should_warn_when_format_lacks_edition_and_a_movie_has_editions()
        {
            Subject.Check().ShouldBeWarning("Edition warning");
        }

        [TestCase("{Movie Title} ({Release Year}) {edition-{Edition Tags}} {Quality Full}")]
        [TestCase("{Movie.Title}.{Release.Year}.{Edition.Tags}")]
        [TestCase("{Movie Title} {[Edition Tags]}")]
        public void should_be_ok_when_format_contains_edition(string format)
        {
            _namingConfig.StandardMovieFormat = format;

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_be_ok_when_no_movie_has_editions()
        {
            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.AnyMovieHasMultipleEditions())
                .Returns(false);

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_be_ok_when_renaming_is_off()
        {
            _namingConfig.RenameMovies = false;

            Subject.Check().ShouldBeOk();
        }
    }
}
