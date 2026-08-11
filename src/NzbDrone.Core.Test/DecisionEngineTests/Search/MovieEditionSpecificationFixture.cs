using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications.Search;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.DecisionEngineTests.Search
{
    [TestFixture]
    public class MovieEditionSpecificationFixture : TestBase<MovieEditionSpecification>
    {
        private RemoteMovie BuildRemote(string parsedEdition = null, string releaseTitle = "Movie.2001.1080p.BluRay")
        {
            return new RemoteMovie
            {
                ParsedMovieInfo = new ParsedMovieInfo { Edition = parsedEdition },
                Release = new ReleaseInfo { Title = releaseTitle }
            };
        }

        private MovieSearchCriteria EditionCriteria(string term) =>
            new MovieSearchCriteria { MovieEditionSlotId = 1, EditionSearchTerm = term };

        [Test]
        public void should_accept_when_no_search_criteria()
        {
            Subject.IsSatisfiedBy(BuildRemote(), null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_normal_movie_search_without_edition_slot()
        {
            var criteria = new MovieSearchCriteria { MovieEditionSlotId = null, EditionSearchTerm = null };
            Subject.IsSatisfiedBy(BuildRemote(), criteria).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_when_edition_slot_set_but_term_empty()
        {
            var criteria = new MovieSearchCriteria { MovieEditionSlotId = 1, EditionSearchTerm = string.Empty };
            var result = Subject.IsSatisfiedBy(BuildRemote(), criteria);

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [Test]
        public void should_accept_when_parsed_edition_matches()
        {
            var remote = BuildRemote(parsedEdition: "Director's Cut");
            Subject.IsSatisfiedBy(remote, EditionCriteria("Director's Cut")).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_release_title_contains_edition_term()
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: "Movie.2001.Directors.Cut.1080p.BluRay");
            Subject.IsSatisfiedBy(remote, EditionCriteria("Director's Cut")).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_reject_when_edition_does_not_match()
        {
            var remote = BuildRemote(parsedEdition: "Theatrical Cut", releaseTitle: "Movie.2001.Theatrical.Cut.1080p");
            var result = Subject.IsSatisfiedBy(remote, EditionCriteria("Director's Cut"));

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [Test]
        public void should_reject_shorter_overlapping_title_term_when_parsed_edition_is_more_specific()
        {
            var remote = BuildRemote(
                parsedEdition: "Extended Director's Cut",
                releaseTitle: "Movie.2001.Extended.Directors.Cut.1080p");
            var result = Subject.IsSatisfiedBy(remote, EditionCriteria("Director's Cut"));

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [Test]
        public void should_reject_when_no_edition_info_at_all()
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: "Movie.2001.1080p.BluRay");
            var result = Subject.IsSatisfiedBy(remote, EditionCriteria("Director's Cut"));

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [TestCase("Director's Cut", "Directors Cut")]
        [TestCase("Director's Cut", "Directors.Cut")]
        [TestCase("Directors Cut", "Director's.Cut")]
        [TestCase("Extended Edition", "Extended.Edition")]
        public void should_accept_with_punctuation_and_space_differences_in_parsed_edition(string parsedEdition, string searchTerm)
        {
            var remote = BuildRemote(parsedEdition: parsedEdition);
            Subject.IsSatisfiedBy(remote, EditionCriteria(searchTerm)).Accepted.Should().BeTrue();
        }

        [TestCase("Movie.2001.Directors.Cut.1080p", "Director's Cut")]
        [TestCase("Movie.2001.Directors-Cut.1080p", "Directors Cut")]
        [TestCase("Movie.2001.Extended.Edition.1080p", "Extended Edition")]
        public void should_accept_with_punctuation_and_space_differences_in_release_title(string releaseTitle, string searchTerm)
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: releaseTitle);
            Subject.IsSatisfiedBy(remote, EditionCriteria(searchTerm)).Accepted.Should().BeTrue();
        }

        // Bug 6 – exact edition matching: substring must not produce false positives

        [TestCase("IMAX Enhanced", "IMAX")]
        [TestCase("IMAX", "IMAX Enhanced")]
        [TestCase("Extended Edition", "Extended")]
        [TestCase("Extended", "Extended Edition")]
        public void should_reject_when_parsed_edition_is_substring_or_superstring_of_wanted(string parsedEdition, string searchTerm)
        {
            var remote = BuildRemote(parsedEdition: parsedEdition);
            var result = Subject.IsSatisfiedBy(remote, EditionCriteria(searchTerm));
            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [TestCase("Movie.2001.CLIMAX.1080p", "IMAX")]
        [TestCase("Movie.2001.ABCD.1080p", "DC")]
        [TestCase("Movie.2001.IMAX.Enhanced.1080p", "IMAX")]
        public void should_reject_release_title_terms_without_an_exact_phrase_boundary(string releaseTitle, string searchTerm)
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: releaseTitle);
            var result = Subject.IsSatisfiedBy(remote, EditionCriteria(searchTerm));

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Be(DownloadRejectionReason.WrongEdition);
        }

        [Test]
        public void should_accept_a_parsed_configured_alias_for_an_explicit_slot_search()
        {
            var criteria = EditionCriteria("Director's Cut");
            criteria.EditionMatchTerms = new System.Collections.Generic.List<string> { "Director's Cut", "DC" };

            Subject.IsSatisfiedBy(BuildRemote(parsedEdition: "DC"), criteria).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_not_match_an_edition_term_that_only_appears_in_the_movie_title()
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: "IMAX.2024.1080p.BluRay");
            remote.ParsedMovieInfo.MovieTitles = new System.Collections.Generic.List<string> { "IMAX" };

            Subject.IsSatisfiedBy(remote, EditionCriteria("IMAX")).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_not_match_an_edition_term_that_only_appears_in_the_release_group()
        {
            var remote = BuildRemote(parsedEdition: null, releaseTitle: "[IMAX].Movie.2024.1080p.BluRay");
            remote.ParsedMovieInfo.ReleaseGroup = "IMAX";

            Subject.IsSatisfiedBy(remote, EditionCriteria("IMAX")).Accepted.Should().BeFalse();
        }

        [TestCase("IMAX", "IMAX")]
        [TestCase("Extended Edition", "Extended Edition")]
        [TestCase("Director's Cut", "Directors Cut")]
        public void should_accept_when_parsed_edition_exactly_matches_wanted(string parsedEdition, string searchTerm)
        {
            var remote = BuildRemote(parsedEdition: parsedEdition);
            Subject.IsSatisfiedBy(remote, EditionCriteria(searchTerm)).Accepted.Should().BeTrue();
        }
    }
}
