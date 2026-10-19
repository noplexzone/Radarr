using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Movies.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.Editions
{
    [TestFixture]
    public class MovieEditionServiceFixture : DbTest<MovieEditionService, MovieEdition>
    {
        private Movie _movie;
        private MediaFileRepository _fileRepository;
        private MovieEditionRepository _editionRepository;

        [SetUp]
        public void Setup()
        {
            _fileRepository = Mocker.Resolve<MediaFileRepository>();
            _editionRepository = Mocker.Resolve<MovieEditionRepository>();

            Mocker.SetConstant<IMediaFileRepository>(_fileRepository);
            Mocker.SetConstant<IMovieEditionRepository>(_editionRepository);
            Mocker.SetConstant<IQualityProfileRepository>(Mocker.Resolve<QualityProfileRepository>());

            Mocker.GetMock<ICustomFormatService>()
                .Setup(x => x.All())
                .Returns(new List<CustomFormat>());

            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.QualityProfileId = 99)
                .With(m => m.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                .Build();
        }

        private MovieFile GivenFile(int editionId, Quality quality, long size = 1000)
        {
            var file = Builder<MovieFile>.CreateNew()
                .With(f => f.Id = 0)
                .With(f => f.MovieId = _movie.Id)
                .With(f => f.MovieEditionId = editionId)
                .With(f => f.Quality = new QualityModel(quality))
                .With(f => f.Size = size)
                .With(f => f.Languages = new List<NzbDrone.Core.Languages.Language>())
                .BuildNew();

            return _fileRepository.Insert(file);
        }

        [Test]
        public void should_create_default_edition_when_movie_is_added()
        {
            Subject.Handle(new MovieAddedEvent(_movie));

            var editions = _editionRepository.All().ToList();

            editions.Should().ContainSingle();
            editions.Single().IsDefault.Should().BeTrue();
            editions.Single().Monitored.Should().BeTrue();
        }

        [Test]
        public void should_not_duplicate_default_edition()
        {
            Subject.Handle(new MovieAddedEvent(_movie));
            Subject.Handle(new MoviesImportedEvent(new List<Movie> { _movie }));

            _editionRepository.All().Should().ContainSingle();
        }

        [Test]
        public void should_create_missing_default_edition_on_read()
        {
            var editions = Subject.GetEditions(_movie.Id);

            editions.Should().ContainSingle(e => e.IsDefault);
            _editionRepository.All().Should().ContainSingle();
        }

        [Test]
        public void should_not_add_edition_with_same_key()
        {
            Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Director's Cut", true));

            Assert.Throws<MovieEditionExistsException>(() => Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Directors Cut", true)));
        }

        [Test]
        public void ensure_edition_should_return_existing_edition()
        {
            var existing = Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));

            var result = Subject.EnsureEdition(MovieEdition.CreateNew(_movie.Id, "Final-Cut", true));

            result.Id.Should().Be(existing.Id);
        }

        [Test]
        public void should_not_delete_default_edition()
        {
            var defaultEdition = Subject.GetDefaultEdition(_movie.Id);

            Assert.Throws<System.InvalidOperationException>(() => Subject.DeleteEdition(defaultEdition));
        }

        [Test]
        public void should_attach_files_to_editions()
        {
            var defaultEdition = Subject.GetDefaultEdition(_movie.Id);
            var finalCut = Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));

            GivenFile(defaultEdition.Id, Quality.Bluray1080p);
            GivenFile(finalCut.Id, Quality.Bluray2160p);
            GivenFile(0, Quality.DVD);

            var editions = Subject.GetEditions(_movie.Id, true);

            editions.Single(e => e.IsDefault).Files.Should().HaveCount(2);
            editions.Single(e => e.Id == finalCut.Id).Files.Should().ContainSingle();
        }

        [Test]
        public void current_file_should_be_best_quality_of_edition_only()
        {
            var defaultEdition = Subject.GetDefaultEdition(_movie.Id);
            var finalCut = Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));

            var dvd = GivenFile(defaultEdition.Id, Quality.DVD);
            var bluray = GivenFile(defaultEdition.Id, Quality.Bluray1080p);
            GivenFile(finalCut.Id, Quality.Bluray2160p);

            Subject.GetCurrentFile(_movie, defaultEdition).Id.Should().Be(bluray.Id);
            Subject.GetCurrentFile(_movie, defaultEdition).Id.Should().NotBe(dvd.Id);
        }

        [Test]
        public void new_edition_should_have_no_current_file()
        {
            GivenFile(0, Quality.DVD);

            Subject.GetCurrentFile(_movie, MovieEdition.CreateNew(_movie.Id, "Final Cut", true)).Should().BeNull();
        }

        [Test]
        public void representative_file_should_prefer_default_edition()
        {
            var defaultEdition = Subject.GetDefaultEdition(_movie.Id);
            var finalCut = Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));

            GivenFile(finalCut.Id, Quality.Bluray2160p);
            var standard = GivenFile(defaultEdition.Id, Quality.DVD);

            Subject.GetRepresentativeFileId(_movie).Should().Be(standard.Id);
        }

        [Test]
        public void representative_file_should_fall_back_to_other_editions()
        {
            Subject.GetDefaultEdition(_movie.Id);
            var finalCut = Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));

            var file = GivenFile(finalCut.Id, Quality.Bluray2160p);

            Subject.GetRepresentativeFileId(_movie).Should().Be(file.Id);
        }

        [Test]
        public void representative_file_should_be_zero_without_files()
        {
            Subject.GetRepresentativeFileId(_movie).Should().Be(0);
        }

        [Test]
        public void should_use_edition_quality_profile_override()
        {
            var profile = Mocker.Resolve<QualityProfileRepository>().Insert(new QualityProfile
            {
                Name = "4K",
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                FormatItems = CustomFormats.CustomFormatsTestHelpers.GetDefaultFormatItems()
            });

            var edition = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            edition.QualityProfileId = profile.Id;

            Subject.GetQualityProfile(_movie, edition).Name.Should().Be("4K");
            Subject.GetQualityProfile(_movie, MovieEdition.CreateNew(_movie.Id, "Theatrical", true)).Should().BeSameAs(_movie.QualityProfile);
        }

        [Test]
        public void should_report_movies_with_multiple_editions()
        {
            Subject.GetDefaultEdition(_movie.Id);
            Subject.AnyMovieHasMultipleEditions().Should().BeFalse();

            Subject.AddEdition(MovieEdition.CreateNew(_movie.Id, "Final Cut", true));
            Subject.AnyMovieHasMultipleEditions().Should().BeTrue();
        }
    }
}
