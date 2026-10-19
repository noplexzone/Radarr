using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras.Others
{
    [TestFixture]
    public class ExistingOtherExtraImporterFixture : CoreTest<ExistingOtherExtraImporter>
    {
        private Movie _movie;
        private List<OtherExtraFile> _upserted;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.Path = @"C:\Movies\Blade Runner (1982)".AsOsAgnostic())
                .With(m => m.MovieFileId = 1)
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<MovieFile>
                {
                    new MovieFile { Id = 1, MovieId = 1, RelativePath = "Blade Runner (1982).mkv" },
                    new MovieFile { Id = 2, MovieId = 1, RelativePath = "Blade Runner (1982) {edition-Final Cut}.mkv" }
                });

            Mocker.GetMock<IExtraFileService<OtherExtraFile>>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<OtherExtraFile>());

            Mocker.GetMock<IExtraFileService<OtherExtraFile>>()
                .Setup(s => s.Upsert(It.IsAny<List<OtherExtraFile>>()))
                .Callback<List<OtherExtraFile>>(f => _upserted = f);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.ParseMinimalPathMovieInfo(It.IsAny<string>()))
                .Returns(new ParsedMovieInfo());
        }

        private int? GivenExtra(string name)
        {
            var path = Path.Combine(_movie.Path, name);

            Subject.ProcessFiles(_movie, new List<string> { path }, new List<string>(), null);

            return _upserted.Single().MovieFileId;
        }

        [TestCase("Blade Runner (1982) {edition-Final Cut}.nfo", 2)]
        [TestCase("Blade Runner (1982) {edition-Final Cut}.en.forced.txt", 2)]
        [TestCase("Blade Runner (1982).nfo", 1)]
        [TestCase("Blade Runner (1982)-trailer.mkv", 1)]
        public void should_attach_extra_to_edition_file_with_matching_name(string name, int movieFileId)
        {
            GivenExtra(name).Should().Be(movieFileId);
        }
    }
}
