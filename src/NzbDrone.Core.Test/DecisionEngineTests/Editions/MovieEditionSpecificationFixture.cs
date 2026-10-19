using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests.Editions
{
    [TestFixture]
    public class MovieEditionSpecificationFixture : CoreTest<MovieEditionSpecification>
    {
        private Movie _movie;
        private MovieEdition _finalCut;
        private RemoteMovie _remoteMovie;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().With(m => m.Id = 1).With(m => m.AutoAddEditions = false).Build();

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            _remoteMovie = new RemoteMovie
            {
                Movie = _movie,
                Release = new ReleaseInfo { Title = "Blade.Runner.1982.Final.Cut.1080p.BluRay.x264-GRP" }
            };
        }

        private void GivenMatch(MovieEditionMatch match)
        {
            _remoteMovie.MovieEditionMatch = match;
            _remoteMovie.MovieEdition = match.Edition;
        }

        private static ReleaseDecisionInformation Rss() => new ReleaseDecisionInformation(false, null);

        private static ReleaseDecisionInformation Search(bool interactive, int movieEditionId = 0)
        {
            return new ReleaseDecisionInformation(false, new MovieSearchCriteria { InteractiveSearch = interactive, UserInvokedSearch = interactive, MovieEditionId = movieEditionId });
        }

        [Test]
        public void should_accept_existing_edition()
        {
            GivenMatch(MovieEditionMatch.Matched(_finalCut));

            Subject.IsSatisfiedBy(_remoteMovie, Rss()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_editions_are_unknown()
        {
            Subject.IsSatisfiedBy(_remoteMovie, Rss()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_ambiguous_release()
        {
            GivenMatch(MovieEditionMatch.Ambiguous("'2in1' contains more than one cut of the movie"));

            var decision = Subject.IsSatisfiedBy(_remoteMovie, Rss());

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.EditionAmbiguous);
        }

        [Test]
        public void should_reject_new_edition_in_rss_when_auto_add_is_off()
        {
            GivenMatch(MovieEditionMatch.New(MovieEdition.CreateNew(_movie.Id, "Director's Cut", true)));

            var decision = Subject.IsSatisfiedBy(_remoteMovie, Rss());

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.EditionNotTracked);
            decision.Message.Should().Contain("New edition: Director's Cut");
        }

        [Test]
        public void should_reject_new_edition_in_automatic_search_when_auto_add_is_off()
        {
            GivenMatch(MovieEditionMatch.New(MovieEdition.CreateNew(_movie.Id, "Director's Cut", true)));

            Subject.IsSatisfiedBy(_remoteMovie, Search(false)).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_new_edition_in_interactive_search()
        {
            GivenMatch(MovieEditionMatch.New(MovieEdition.CreateNew(_movie.Id, "Director's Cut", true)));

            Subject.IsSatisfiedBy(_remoteMovie, Search(true)).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_new_edition_when_auto_add_is_on()
        {
            _movie.AutoAddEditions = true;
            GivenMatch(MovieEditionMatch.New(MovieEdition.CreateNew(_movie.Id, "Director's Cut", true)));

            Subject.IsSatisfiedBy(_remoteMovie, Rss()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_release_for_other_edition_than_searched()
        {
            GivenMatch(MovieEditionMatch.Matched(_finalCut));

            var decision = Subject.IsSatisfiedBy(_remoteMovie, Search(true, 7));

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [Test]
        public void should_accept_release_for_searched_edition()
        {
            GivenMatch(MovieEditionMatch.Matched(_finalCut));

            Subject.IsSatisfiedBy(_remoteMovie, Search(false, _finalCut.Id)).Accepted.Should().BeTrue();
        }
    }
}
