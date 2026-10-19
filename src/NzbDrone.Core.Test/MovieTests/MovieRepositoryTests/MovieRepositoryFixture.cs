using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.MovieRepositoryTests
{
    [TestFixture]

    public class MovieRepositoryFixture : DbTest<MovieRepository, Movie>
    {
        private IQualityProfileRepository _profileRepository;

        [SetUp]
        public void Setup()
        {
            _profileRepository = Mocker.Resolve<QualityProfileRepository>();
            Mocker.SetConstant<IQualityProfileRepository>(_profileRepository);

            Mocker.GetMock<ICustomFormatService>()
                .Setup(x => x.All())
                .Returns(new List<CustomFormat>());
        }

        [Test]
        public void should_load_quality_profile()
        {
            var profile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(Quality.Bluray1080p, Quality.DVD, Quality.HDTV720p),
                FormatItems = CustomFormatsTestHelpers.GetDefaultFormatItems(),
                MinFormatScore = 0,
                Cutoff = Quality.Bluray1080p.Id,
                Name = "TestProfile"
            };

            _profileRepository.Insert(profile);

            var movie = Builder<Movie>.CreateNew().BuildNew();
            movie.QualityProfileId = profile.Id;

            Subject.Insert(movie);

            Subject.All().Single().QualityProfile.Should().NotBeNull();
        }

        [Test]
        public void should_load_main_file_when_movie_has_several_editions()
        {
            var profile = _profileRepository.Insert(new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = CustomFormatsTestHelpers.GetDefaultFormatItems(),
                Name = "TestProfile"
            });

            var metadata = Mocker.Resolve<MovieMetadataRepository>().Insert(Builder<MovieMetadata>.CreateNew().BuildNew());
            var movie = Builder<Movie>.CreateNew()
                .With(m => m.QualityProfileId = profile.Id)
                .With(m => m.MovieMetadataId = metadata.Id)
                .BuildNew();
            Subject.Insert(movie);

            var fileRepository = Mocker.Resolve<MediaFileRepository>();
            var files = Builder<MovieFile>.CreateListOfSize(3)
                .All()
                .With(f => f.Id = 0)
                .With(f => f.MovieId = movie.Id)
                .With(f => f.Quality = new QualityModel(Quality.Bluray1080p))
                .With(f => f.Languages = new List<NzbDrone.Core.Languages.Language>())
                .BuildListOfNew();

            fileRepository.InsertMany(files);

            var main = fileRepository.All().OrderBy(f => f.Id).Skip(1).First();
            movie.MovieFileId = main.Id;
            Subject.Update(movie);

            Subject.Get(movie.Id).MovieFile.Id.Should().Be(main.Id);
        }
    }
}
