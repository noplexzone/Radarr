using System.Collections.Generic;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MovieImport
{
    [TestFixture]
    public class ImportApprovedMoviesEditionFixture : CoreTest<ImportApprovedMovie>
    {
        private Movie _movie;
        private MovieEdition _default;
        private MovieEdition _finalCut;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                .With(m => m.Path = @"C:\Test\Movies\Blade Runner (1982)".AsOsAgnostic())
                .Build();

            _default = MovieEdition.CreateDefault(_movie.Id);
            _default.Id = 1;

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            Mocker.GetMock<IUpgradeMediaFiles>()
                .Setup(s => s.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()))
                .Returns(new MovieFileMoveResult());

            Mocker.GetMock<IHistoryService>()
                .Setup(x => x.FindByDownloadId(It.IsAny<string>()))
                .Returns(new List<MovieHistory>());

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.Add(It.IsAny<MovieFile>()))
                .Returns<MovieFile>(f => f);

            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.EnsureEdition(It.IsAny<MovieEdition>()))
                .Returns<MovieEdition>(e =>
                {
                    if (e.IsNew)
                    {
                        e.Id = 3;
                    }

                    return e;
                });
        }

        private ImportDecision GivenDecision(string name, MovieEdition edition, Quality quality)
        {
            return new ImportDecision(new LocalMovie
            {
                Movie = _movie,
                Path = Path.Combine(@"C:\Test\Downloads".AsOsAgnostic(), name),
                Quality = new QualityModel(quality),
                MovieEdition = edition
            });
        }

        [Test]
        public void should_import_one_file_per_edition_from_a_pack()
        {
            var decisions = new List<ImportDecision>
            {
                GivenDecision("Blade.Runner.1982.Theatrical.mkv", _default, Quality.Bluray1080p),
                GivenDecision("Blade.Runner.1982.Final.Cut.mkv", _finalCut, Quality.Bluray1080p),
                GivenDecision("Blade.Runner.1982.Directors.Cut.mkv", MovieEdition.CreateNew(_movie.Id, "Director's Cut", true), Quality.Bluray1080p)
            };

            var results = Subject.Import(decisions, true);

            results.Where(r => r.Result == ImportResultType.Imported).Should().HaveCount(3);
        }

        [Test]
        public void should_import_only_best_file_of_the_same_edition()
        {
            var decisions = new List<ImportDecision>
            {
                GivenDecision("Blade.Runner.1982.Final.Cut.720p.mkv", _finalCut, Quality.HDTV720p),
                GivenDecision("Blade.Runner.1982.Final.Cut.1080p.mkv", _finalCut, Quality.Bluray1080p)
            };

            var results = Subject.Import(decisions, true);

            results.Should().ContainSingle(r => r.Result == ImportResultType.Imported)
                .Which.ImportDecision.LocalMovie.Quality.Quality.Should().Be(Quality.Bluray1080p);
        }

        [Test]
        public void should_store_edition_on_imported_file()
        {
            Subject.Import(new List<ImportDecision> { GivenDecision("Blade.Runner.1982.Final.Cut.mkv", _finalCut, Quality.Bluray1080p) }, true);

            Mocker.GetMock<IMediaFileService>()
                .Verify(v => v.Add(It.Is<MovieFile>(f => f.MovieEditionId == _finalCut.Id && f.Edition == "Final Cut")), Times.Once());
        }

        [Test]
        public void should_create_new_edition_before_import()
        {
            var decision = GivenDecision("Blade.Runner.1982.Directors.Cut.mkv", MovieEdition.CreateNew(_movie.Id, "Director's Cut", true), Quality.Bluray1080p);

            Subject.Import(new List<ImportDecision> { decision }, true);

            Mocker.GetMock<IMovieEditionService>().Verify(v => v.EnsureEdition(It.Is<MovieEdition>(e => e.Key == "director's")), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(f => f.MovieEditionId == 3)), Times.Once());
        }

        [Test]
        public void should_not_import_unknown_edition_into_multi_edition_movie()
        {
            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetEditions(_movie.Id, false))
                .Returns(new List<MovieEdition> { _default, _finalCut });

            var results = Subject.Import(new List<ImportDecision> { GivenDecision("Blade.Runner.1982.mkv", null, Quality.Bluray1080p) }, true);

            results.Should().ContainSingle().Which.Result.Should().NotBe(ImportResultType.Imported);
            Mocker.GetMock<IUpgradeMediaFiles>().Verify(v => v.UpgradeMovieFile(It.IsAny<MovieFile>(), It.IsAny<LocalMovie>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_import_unknown_edition_into_single_edition_movie()
        {
            Mocker.GetMock<IMovieEditionService>()
                .Setup(s => s.GetEditions(_movie.Id, false))
                .Returns(new List<MovieEdition> { _default });

            var results = Subject.Import(new List<ImportDecision> { GivenDecision("Blade.Runner.1982.mkv", null, Quality.Bluray1080p) }, true);

            results.Should().ContainSingle().Which.Result.Should().Be(ImportResultType.Imported);
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Add(It.Is<MovieFile>(f => f.MovieEditionId == _default.Id)), Times.Once());
        }
    }
}
