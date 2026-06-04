using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class MovieSearchCriteriaEditionFixture
    {
        private static List<string> BaseTitles() => new List<string> { "Blade Runner", "Blade Runner 1982" };

        [Test]
        public void null_edition_returns_copy_of_base_titles()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(BaseTitles(), null);
            result.Should().Equal("Blade Runner", "Blade Runner 1982");
        }

        [Test]
        public void empty_edition_returns_copy_of_base_titles()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(BaseTitles(), "");
            result.Should().Equal("Blade Runner", "Blade Runner 1982");
        }

        [Test]
        public void whitespace_edition_returns_copy_of_base_titles()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(BaseTitles(), "   ");
            result.Should().Equal("Blade Runner", "Blade Runner 1982");
        }

        [Test]
        public void edition_variants_are_prepended_before_base_titles()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(BaseTitles(), "Final Cut");
            result.Should().Equal(
                "Blade Runner Final Cut",
                "Blade Runner 1982 Final Cut",
                "Blade Runner",
                "Blade Runner 1982");
        }

        [Test]
        public void edition_term_is_trimmed()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(new List<string> { "Dune" }, "  Extended  ");
            result.Should().Equal("Dune Extended", "Dune");
        }

        [Test]
        public void empty_base_list_returns_empty_list()
        {
            var result = MovieSearchCriteria.BuildEditionTitles(new List<string>(), "Director's Cut");
            result.Should().BeEmpty();
        }

        [Test]
        public void input_list_is_not_mutated()
        {
            var original = new List<string> { "Alien" };
            MovieSearchCriteria.BuildEditionTitles(original, "Theatrical");
            original.Should().HaveCount(1);
            original[0].Should().Be("Alien");
        }

        [Test]
        public void null_edition_result_is_a_new_list_not_same_reference()
        {
            var original = BaseTitles();
            var result = MovieSearchCriteria.BuildEditionTitles(original, null);
            result.Should().NotBeSameAs(original);
        }
    }
}
