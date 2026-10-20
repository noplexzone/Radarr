using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.Editions
{
    [TestFixture]
    public class EditionNormalizerFixture : CoreTest
    {
        [TestCase("Director's Cut", "director's")]
        [TestCase("Directors Cut", "director's")]
        [TestCase("Directors.Cut", "director's")]
        [TestCase("DIRECTORS CUT", "director's")]
        [TestCase("Director’s Cut", "director's")]
        [TestCase("Director's Edition", "director's")]
        [TestCase("Final Cut", "final")]
        [TestCase("The Final Cut", "final")]
        [TestCase("The.Final.Cut", "final")]
        [TestCase("The Cut", "cut")]
        [TestCase("The", "the")]
        [TestCase("Theatrical", "theatrical")]
        [TestCase("Theatrical Cut", "theatrical")]
        [TestCase("Extended Edition", "extended")]
        [TestCase("Extended Cut", "extended")]
        [TestCase("Extended", "extended")]
        [TestCase("ExtCut", "extended")]
        [TestCase("25th Anniversary Edition", "25th anniversary")]
        [TestCase("Black & Chrome", "black and chrome")]
        [TestCase("Black and Chrome", "black and chrome")]
        [TestCase("Collectors Edition", "collector's")]
        [TestCase("Open-Matte", "open matte")]
        [TestCase("Cut", "cut")]
        [TestCase("  Unrated  ", "unrated")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void should_normalize_edition(string edition, string expected)
        {
            EditionNormalizer.Normalize(edition).Should().Be(expected);
        }

        [Test]
        public void should_keep_distinct_editions_distinct()
        {
            EditionNormalizer.Normalize("Director's Cut").Should().NotBe(EditionNormalizer.Normalize("Final Cut"));
            EditionNormalizer.Normalize("Extended").Should().NotBe(EditionNormalizer.Normalize("Extended Director's Cut"));
        }
    }
}
