using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MovieTests.Editions
{
    [TestFixture]
    public class EditionTitleFixture : CoreTest
    {
        [TestCase("UNRATED", "Unrated")]
        [TestCase("UNCUT", "Uncut")]
        [TestCase("EXTENDED", "Extended")]
        [TestCase("RESTORED", "Restored")]
        [TestCase("REMASTERED", "Remastered")]
        [TestCase("Directors Cut", "Director's Cut")]
        [TestCase("DIRECTORS CUT", "Director's Cut")]
        [TestCase("Directors.Cut", "Director's Cut")]
        [TestCase("Collectors Edition", "Collector's Edition")]
        [TestCase("Director's Cut", "Director's Cut")]
        [TestCase("EXTENDED REMASTERED", "Extended Remastered")]
        [TestCase("ExtCut", "Extended Cut")]
        [TestCase("IMAX", "IMAX")]
        [TestCase("imax", "IMAX")]
        [TestCase("US Theatrical Cut", "US Theatrical Cut")]
        [TestCase("25TH ANNIVERSARY EDITION", "25th Anniversary Edition")]
        [TestCase("Theatrical 2in1", "Theatrical 2in1")]
        [TestCase("OPEN MATTE", "Open Matte")]
        [TestCase("THE FINAL CUT", "The Final Cut")]
        [TestCase("SPECIAL EDITION OF THE YEAR", "Special Edition of the Year")]
        [TestCase("McDonald Cut", "McDonald Cut")]
        [TestCase("Fan Edit", "Fan Edit")]
        public void should_format_title(string edition, string expected)
        {
            EditionTitle.Format(edition).Should().Be(expected);
        }

        [TestCase(null)]
        [TestCase("")]
        public void should_leave_empty_titles(string edition)
        {
            EditionTitle.Format(edition).Should().Be(edition);
        }
    }
}
