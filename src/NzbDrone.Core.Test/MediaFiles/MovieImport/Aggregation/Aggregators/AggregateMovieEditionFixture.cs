using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation.Aggregators;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport.Aggregation.Aggregators
{
    [TestFixture]
    public class AggregateMovieEditionFixture : CoreTest<AggregateMovieEdition>
    {
        private Movie _movie;
        private MovieEdition _default;
        private MovieEdition _finalCut;
        private LocalMovie _localMovie;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 1).Build();

            _default = MovieEdition.CreateDefault(_movie.Id);
            _default.Id = 1;

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetEditions(_movie.Id, false))
                .Returns(new List<MovieEdition> { _default, _finalCut });

            _localMovie = new LocalMovie { Movie = _movie, Path = "/Movies/Blade Runner (1982)/file.mkv" };
        }

        private void GivenGrabbedEdition(int editionId)
        {
            var history = new MovieHistory
            {
                SourceTitle = "Blade.Runner.1982.1080p",
                Data = new Dictionary<string, string> { { "movieEditionId", editionId.ToString() } }
            };

            _localMovie.Release = new GrabbedReleaseInfo(new List<MovieHistory> { history });
        }

        [Test]
        public void should_use_default_edition_for_untagged_file()
        {
            Subject.Aggregate(_localMovie, null).MovieEdition.Should().Be(_default);
        }

        [Test]
        public void should_match_existing_edition()
        {
            _localMovie.Edition = "Final.Cut";

            Subject.Aggregate(_localMovie, null).MovieEdition.Should().Be(_finalCut);
        }

        [Test]
        public void should_use_new_edition_for_unknown_edition()
        {
            _localMovie.Edition = "Director's Cut";

            var edition = Subject.Aggregate(_localMovie, null).MovieEdition;

            edition.IsNew.Should().BeTrue();
            edition.Key.Should().Be("director's");
        }

        [Test]
        public void should_not_resolve_multi_cut_release()
        {
            _localMovie.Edition = "2in1";

            var result = Subject.Aggregate(_localMovie, null);

            result.MovieEdition.Should().BeNull();
            result.MovieEditionRejection.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void should_use_grabbed_edition_for_untagged_file()
        {
            GivenGrabbedEdition(_finalCut.Id);

            Subject.Aggregate(_localMovie, null).MovieEdition.Should().Be(_finalCut);
        }

        [Test]
        public void should_reject_when_file_conflicts_with_grabbed_edition()
        {
            GivenGrabbedEdition(_default.Id);
            _localMovie.Edition = "Final Cut";

            var result = Subject.Aggregate(_localMovie, null);

            result.MovieEdition.Should().BeNull();
            result.MovieEditionRejection.Should().Contain("Final Cut");
        }

        [Test]
        public void should_ignore_grabbed_edition_that_no_longer_exists()
        {
            GivenGrabbedEdition(99);
            _localMovie.Edition = "Final Cut";

            Subject.Aggregate(_localMovie, null).MovieEdition.Should().Be(_finalCut);
        }

        [Test]
        public void should_not_override_manual_selection()
        {
            _localMovie.Edition = "Final Cut";
            _localMovie.MovieEdition = _default;

            Subject.Aggregate(_localMovie, null).MovieEdition.Should().Be(_default);
        }
    }
}
