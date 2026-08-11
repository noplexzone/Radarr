using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class MovieEditionReleaseSearchFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;
        private Movie _movie;

        [SetUp]
        public void SetUp()
        {
            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                .Setup(s => s.AutomaticSearchEnabled(true))
                .Returns(new List<IIndexer> { _mockIndexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision>());

            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Title = "Dune")
                .Build();

            _movie.MovieMetadata.Value.OriginalTitle = null;

            Mocker.GetMock<IMovieService>()
                .Setup(v => v.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMovieTranslationService>()
                .Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                .Returns(new List<MovieTranslation>());
        }

        private List<MovieSearchCriteria> WatchForEditionSearchCriteria()
        {
            var result = new List<MovieSearchCriteria>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Callback<MovieSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>()));

            return result;
        }

        [Test]
        public async Task Edition_search_prepends_edition_variant_before_base_title()
        {
            var slot = new MovieEditionSlot { Id = 1, MovieId = _movie.Id, SearchTerm = "IMAX", Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;
            criteria.SceneTitles.First().Should().Be("Dune IMAX");
            criteria.SceneTitles.Should().Contain("Dune");
            criteria.EditionSearchTerm.Should().Be("IMAX");
            criteria.MovieEditionSlotId.Should().Be(1);
        }

        [Test]
        public async Task Edition_search_without_search_term_keeps_base_titles_unchanged()
        {
            var slot = new MovieEditionSlot { Id = 2, MovieId = _movie.Id, SearchTerm = null, Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;
            criteria.SceneTitles.Should().ContainSingle().Which.Should().Be("Dune");
            criteria.EditionSearchTerm.Should().BeNull();
            criteria.MovieEditionSlotId.Should().Be(2);
        }

        [Test]
        public async Task Edition_search_with_multiple_base_titles_prepends_all_edition_variants()
        {
            _movie.MovieMetadata.Value.OriginalTitle = "Dyuna";

            var slot = new MovieEditionSlot { Id = 3, MovieId = _movie.Id, SearchTerm = "4K", Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;

            // Edition variants appear first
            criteria.SceneTitles.IndexOf("Dune 4K").Should().BeLessThan(criteria.SceneTitles.IndexOf("Dune"));
            criteria.SceneTitles.IndexOf("Dyuna 4K").Should().BeLessThan(criteria.SceneTitles.IndexOf("Dyuna"));
            criteria.SceneTitles.Should().Contain("Dune 4K");
            criteria.SceneTitles.Should().Contain("Dyuna 4K");
            criteria.SceneTitles.Should().Contain("Dune");
            criteria.SceneTitles.Should().Contain("Dyuna");
        }

        [Test]
        public async Task Edition_search_sets_override_quality_profile_when_slot_has_quality_profile_id()
        {
            var slotProfile = new QualityProfile { Id = 99, Name = "Slot Profile" };

            Mocker.GetMock<NzbDrone.Core.Profiles.Qualities.IQualityProfileService>()
                .Setup(s => s.Get(99))
                .Returns(slotProfile);

            var slot = new MovieEditionSlot { Id = 5, MovieId = _movie.Id, SearchTerm = "IMAX", QualityProfileId = 99, Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;
            criteria.OverrideQualityProfile.Should().BeSameAs(slotProfile);
        }

        [Test]
        public async Task Edition_search_leaves_override_quality_profile_null_when_slot_has_no_quality_profile_id()
        {
            var slot = new MovieEditionSlot { Id = 6, MovieId = _movie.Id, QualityProfileId = null, Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;
            criteria.OverrideQualityProfile.Should().BeNull();
        }

        [Test]
        public async Task Edition_search_sets_slot_minimum_custom_format_score_when_slot_has_value()
        {
            var slot = new MovieEditionSlot { Id = 7, MovieId = _movie.Id, MinimumCustomFormatScore = 50, Monitored = true };

            var captured = WatchForEditionSearchCriteria();

            await Subject.MovieEditionSearch(_movie, slot, true, false);

            var criteria = captured.Should().ContainSingle().Subject;
            criteria.SlotMinimumCustomFormatScore.Should().Be(50);
        }
    }

    [TestFixture]
    public class MovieEditionSearchServiceFixture : CoreTest<MovieSearchService>
    {
        private Movie _movie;
        private List<MovieEditionSlot> _slots;

        [SetUp]
        public void SetUp()
        {
            _movie = Builder<Movie>.CreateNew()
                .With(v => v.Monitored = true)
                .With(v => v.Title = "Dune")
                .Build();

            _slots = new List<MovieEditionSlot>
            {
                new MovieEditionSlot { Id = 1, MovieId = _movie.Id, Monitored = true },
                new MovieEditionSlot { Id = 2, MovieId = _movie.Id, Monitored = true },
                new MovieEditionSlot { Id = 3, MovieId = _movie.Id, Monitored = true },  // has file
                new MovieEditionSlot { Id = 4, MovieId = _movie.Id, Monitored = false }  // unmonitored
            };

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovie(_movie.Id))
                .Returns(_movie);

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetForMovie(_movie.Id))
                .Returns(_slots);

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovie(_movie.Id))
                .Returns(new List<MovieFile>
                {
                    new MovieFile { Id = 10, MovieId = _movie.Id, MovieEditionSlotId = 3 }
                });

            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Returns(Task.FromResult(new List<DownloadDecision>()));

            Mocker.GetMock<IProcessDownloadDecisions>()
                .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .Returns(Task.FromResult(new ProcessedDecisions(
                    new List<DownloadDecision>(),
                    new List<DownloadDecision>(),
                    new List<DownloadDecision>())));
        }

        [Test]
        public void Execute_with_no_explicit_ids_searches_monitored_missing_slots_only()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id });

            var mock = Mocker.GetMock<ISearchForReleases>();

            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 1), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 2), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 3), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 4), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void Execute_manual_trigger_includes_unmonitored_missing_slots()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id, Trigger = CommandTrigger.Manual });

            var mock = Mocker.GetMock<ISearchForReleases>();

            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 1), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 2), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 3), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 4), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
        }

        [Test]
        public void Execute_with_explicit_slot_id_searches_only_that_slot()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id, MovieEditionSlotId = 3 });

            var mock = Mocker.GetMock<ISearchForReleases>();

            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 3), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            mock.Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id != 3), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void Execute_with_explicit_unmonitored_slot_skips_it_unless_manual()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id, MovieEditionSlotId = 4 });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void Execute_with_explicit_unmonitored_slot_searches_it_when_manual()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id, MovieEditionSlotId = 4, Trigger = CommandTrigger.Manual });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.Is<MovieEditionSlot>(sl => sl.Id == 4), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
        }

        [Test]
        public void Execute_updates_slot_last_search_time_after_each_search()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id });

            Mocker.GetMock<IMovieEditionSlotService>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(sl => sl.Id == 1 && sl.LastSearchTime.HasValue)), Times.Once);

            Mocker.GetMock<IMovieEditionSlotService>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(sl => sl.Id == 2 && sl.LastSearchTime.HasValue)), Times.Once);
        }

        [Test]
        public void Execute_with_explicit_slot_updates_only_that_slots_last_search_time()
        {
            Subject.Execute(new MovieEditionSearchCommand { MovieId = _movie.Id, MovieEditionSlotId = 2 });

            Mocker.GetMock<IMovieEditionSlotService>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(sl => sl.Id == 2 && sl.LastSearchTime.HasValue)), Times.Once);
            Mocker.GetMock<IMovieEditionSlotService>()
                .Verify(s => s.Update(It.Is<MovieEditionSlot>(sl => sl.Id != 2)), Times.Never);
        }
    }
}
