using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download.Aggregation.Aggregators;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Aggregation.Aggregators
{
    [TestFixture]
    public class AggregateRemoteMovieEditionFixture : CoreTest<AggregateRemoteMovieEdition>
    {
        private Movie _movie;
        private MovieEdition _default;
        private MovieEdition _finalCut;
        private MovieFile _finalCutFile;
        private QualityProfile _uhdProfile;
        private RemoteMovie _remoteMovie;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.QualityProfile = new QualityProfile { Name = "HD" })
                .Build();

            _default = MovieEdition.CreateDefault(_movie.Id);
            _default.Id = 1;

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            _finalCutFile = new MovieFile { Id = 5, MovieEditionId = _finalCut.Id };
            _uhdProfile = new QualityProfile { Name = "UHD" };

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetEditions(_movie.Id, false))
                .Returns(new List<MovieEdition> { _default, _finalCut });

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetCurrentFile(_movie, _finalCut))
                .Returns(_finalCutFile);

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetQualityProfile(_movie, It.IsAny<MovieEdition>()))
                .Returns<Movie, MovieEdition>((m, e) => e == _finalCut ? _uhdProfile : m.QualityProfile);

            _remoteMovie = new RemoteMovie { Movie = _movie, ParsedMovieInfo = new ParsedMovieInfo() };
        }

        [Test]
        public void should_use_default_edition_for_untagged_release()
        {
            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().Be(_default);
            _remoteMovie.EditionFile.Should().BeNull();
            _remoteMovie.QualityProfile.Name.Should().Be("HD");
        }

        [Test]
        public void should_use_edition_file_and_profile_for_tagged_release()
        {
            _remoteMovie.ParsedMovieInfo.Edition = "Final Cut";

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().Be(_finalCut);
            _remoteMovie.ExistingFile.Should().Be(_finalCutFile);
            _remoteMovie.QualityProfile.Should().Be(_uhdProfile);
        }

        [Test]
        public void should_compare_new_edition_with_nothing()
        {
            _movie.MovieFile = new MovieFile { Id = 9 };
            _remoteMovie.ParsedMovieInfo.Edition = "Director's Cut";

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.IsNew.Should().BeTrue();
            _remoteMovie.ExistingFile.Should().BeNull();
        }

        [Test]
        public void should_leave_ambiguous_release_without_edition()
        {
            _remoteMovie.ParsedMovieInfo.Edition = "4in1";

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().BeNull();
            _remoteMovie.MovieEditionMatch.IsResolved.Should().BeFalse();
        }

        [Test]
        public void should_keep_edition_restored_from_grab()
        {
            _remoteMovie.MovieEdition = new MovieEdition { Id = _finalCut.Id, MovieId = _movie.Id };

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().Be(_finalCut);
        }

        [Test]
        public void should_match_again_when_restored_edition_was_deleted()
        {
            _remoteMovie.MovieEdition = new MovieEdition { Id = 99, MovieId = _movie.Id };

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().Be(_default);
        }

        [Test]
        public void should_use_created_edition_for_pending_new_edition()
        {
            _remoteMovie.MovieEdition = MovieEdition.CreateNew(_movie.Id, "Final-Cut", true);

            Subject.Aggregate(_remoteMovie);

            _remoteMovie.MovieEdition.Should().Be(_finalCut);
        }
    }
}
