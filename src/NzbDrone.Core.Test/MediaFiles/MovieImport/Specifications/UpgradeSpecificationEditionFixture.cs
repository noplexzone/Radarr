using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport.Specifications;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Specifications
{
    [TestFixture]
    public class UpgradeSpecificationEditionFixture : CoreTest<UpgradeSpecification>
    {
        private Movie _movie;
        private MovieEdition _finalCut;
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                .Build();

            // The movie's main file is a much better Theatrical release
            _movie.MovieFileId = 1;
            _movie.MovieFile = new MovieFile { Id = 1, Quality = new QualityModel(Quality.Bluray2160p) };

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            _localMovie = new LocalMovie
            {
                Path = "/Movies/Blade Runner (1982)/Blade Runner {edition-Final Cut}.mkv",
                Quality = new QualityModel(Quality.HDTV720p),
                Movie = _movie,
                MovieEdition = _finalCut
            };

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetQualityProfile(_movie, It.IsAny<MovieEdition>()))
                .Returns(_movie.QualityProfile);
        }

        private void GivenEditionFile(Quality quality)
        {
            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetCurrentFile(_movie, _finalCut))
                .Returns(new MovieFile { Id = 2, MovieEditionId = _finalCut.Id, Quality = new QualityModel(quality) });
        }

        [Test]
        public void should_accept_empty_edition_even_when_other_edition_is_better()
        {
            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_new_edition()
        {
            _localMovie.MovieEdition = MovieEdition.CreateNew(_movie.Id, "Director's Cut", true);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_upgrade_of_edition_file()
        {
            GivenEditionFile(Quality.SDTV);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_when_not_an_upgrade_of_edition_file()
        {
            GivenEditionFile(Quality.Bluray1080p);

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_existing_file_in_folder_as_duplicate()
        {
            GivenEditionFile(Quality.Bluray1080p);
            _localMovie.ExistingFile = true;

            Subject.IsSatisfiedBy(_localMovie, null).Accepted.Should().BeTrue();
        }
    }
}
