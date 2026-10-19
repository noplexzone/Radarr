using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.Editions
{
    [TestFixture]
    public class MovieEditionMatcherFixture : CoreTest
    {
        private List<MovieEdition> _editions;

        [SetUp]
        public void Setup()
        {
            _editions = new List<MovieEdition>
            {
                new MovieEdition { Id = 1, MovieId = 1, IsDefault = true, Key = string.Empty },
                new MovieEdition { Id = 2, MovieId = 1, Title = "Director's Cut", Key = EditionNormalizer.Normalize("Director's Cut") },
                new MovieEdition { Id = 3, MovieId = 1, Title = "Final Cut", Key = EditionNormalizer.Normalize("Final Cut") }
            };
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Remastered")]
        [TestCase("REMASTERED")]
        public void should_match_default_edition(string parsed)
        {
            var match = MovieEditionMatcher.Match(1, _editions, parsed);

            match.Type.Should().Be(MovieEditionMatchType.Default);
            match.Edition.Id.Should().Be(1);
        }

        [TestCase("Directors Cut", 2)]
        [TestCase("Director's Cut", 2)]
        [TestCase("DIRECTORS.CUT", 2)]
        [TestCase("Director's Edition", 2)]
        [TestCase("Final Cut", 3)]
        [TestCase("The Final Cut", 3)]
        [TestCase("Final Cut Remastered", 3)]
        [TestCase("Remastered Final Cut", 3)]
        public void should_match_existing_edition(string parsed, int editionId)
        {
            var match = MovieEditionMatcher.Match(1, _editions, parsed);

            match.Type.Should().Be(MovieEditionMatchType.Matched);
            match.Edition.Id.Should().Be(editionId);
        }

        [Test]
        public void should_match_edition_named_with_leading_article()
        {
            _editions[2].Title = "The Final Cut";
            _editions[2].Key = EditionNormalizer.Normalize("The Final Cut");

            var match = MovieEditionMatcher.Match(1, _editions, "Final Cut");

            match.Type.Should().Be(MovieEditionMatchType.Matched);
            match.Edition.Id.Should().Be(3);
        }

        [Test]
        public void should_match_renamed_default_edition()
        {
            _editions[0].Title = "Theatrical";
            _editions[0].Key = EditionNormalizer.Normalize("Theatrical");

            var match = MovieEditionMatcher.Match(1, _editions, "Theatrical Cut");

            match.Type.Should().Be(MovieEditionMatchType.Default);
            match.Edition.Id.Should().Be(1);
        }

        [TestCase("IMAX")]
        [TestCase("Extended")]
        [TestCase("Criterion")]
        [TestCase("Final Cut IMAX")]
        public void should_return_new_edition_for_unknown_edition(string parsed)
        {
            var match = MovieEditionMatcher.Match(1, _editions, parsed, false);

            match.Type.Should().Be(MovieEditionMatchType.New);
            match.Edition.IsNew.Should().BeTrue();
            match.Edition.Title.Should().Be(parsed);
            match.Edition.MovieId.Should().Be(1);
            match.Edition.Monitored.Should().BeFalse();
        }

        [TestCase("2in1")]
        [TestCase("3in1")]
        [TestCase("Theatrical 2in1")]
        [TestCase("Director's Cut Final Cut")]
        [TestCase("Final Cut Directors Cut")]
        [TestCase("Theatrical Cut Extended Cut")]
        public void should_be_ambiguous(string parsed)
        {
            var match = MovieEditionMatcher.Match(1, _editions, parsed);

            match.Type.Should().Be(MovieEditionMatchType.Ambiguous);
            match.IsResolved.Should().BeFalse();
            match.Reason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
