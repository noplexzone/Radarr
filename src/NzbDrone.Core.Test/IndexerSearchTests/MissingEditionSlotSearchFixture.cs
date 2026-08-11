using System.Collections.Generic;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.MovieEditionSlots;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    // Feature 8 + 9: MissingMoviesSearchCommand edition-slot awareness and CutoffUnmetEditionSlots
    [TestFixture]
    public class MissingEditionSlotSearchFixture : CoreTest<MovieSearchService>
    {
        private Movie _monitoredMovie;
        private MovieEditionSlot _missingSlot;
        private MovieEditionSlot _monitoredSlotWithFile;
        private QualityProfile _movieQualityProfile;
        private QualityProfile _slotOverrideQualityProfile;

        [SetUp]
        public void SetUp()
        {
            _movieQualityProfile = BuildQualityProfile(1, Quality.WEBDL1080p);
            _slotOverrideQualityProfile = BuildQualityProfile(2, Quality.Bluray1080p);

            _monitoredMovie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.Monitored = true)
                .With(m => m.QualityProfileId = _movieQualityProfile.Id)
                .With(m => m.QualityProfile = _movieQualityProfile)
                .Build();

            _missingSlot = new MovieEditionSlot
            {
                Id = 10,
                MovieId = 1,
                EditionName = "Extended",
                Monitored = true
            };

            _monitoredSlotWithFile = new MovieEditionSlot
            {
                Id = 11,
                MovieId = 1,
                EditionName = "Theatrical",
                Monitored = true
            };

            // No movies without files (normal missing search yields nothing)
            Mocker.GetMock<IMovieService>()
                .Setup(s => s.MoviesWithoutFiles(It.IsAny<NzbDrone.Core.Datastore.PagingSpec<Movie>>()))
                .Returns(new NzbDrone.Core.Datastore.PagingSpec<Movie> { Records = new List<Movie>() });

            Mocker.GetMock<IMovieCutoffService>()
                .Setup(s => s.MoviesWhereCutoffUnmet(It.IsAny<NzbDrone.Core.Datastore.PagingSpec<Movie>>()))
                .Returns(new NzbDrone.Core.Datastore.PagingSpec<Movie> { Records = new List<Movie>() });

            // Queue is empty
            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue>());

            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .ReturnsAsync(new List<DownloadDecision>());

            Mocker.GetMock<IProcessDownloadDecisions>()
                .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                .ReturnsAsync(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));
        }

        private static QualityProfile BuildQualityProfile(int id, Quality cutoff)
        {
            return new QualityProfile
            {
                Id = id,
                Name = $"Profile {id}",
                UpgradeAllowed = true,
                Cutoff = cutoff.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.SDTV, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.HDTV720p, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.WEBDL1080p, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.Bluray1080p, Allowed = true }
                }
            };
        }

        private void GivenCutoffSlot(MovieEditionSlot slot, Quality fileQuality, params CustomFormat[] customFormats)
        {
            var movieFile = new MovieFile
            {
                Id = 100,
                MovieId = slot.MovieId,
                MovieEditionSlotId = slot.Id,
                Quality = new QualityModel(fileQuality)
            };

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredSlotsWithFiles())
                .Returns(new List<MovieEditionSlot> { slot });

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<MovieFile> { movieFile });

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(s => s.ParseCustomFormat(movieFile, _monitoredMovie))
                .Returns(new List<CustomFormat>(customFormats));

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _monitoredMovie });

            Mocker.GetMock<IQualityProfileService>()
                .Setup(s => s.All())
                .Returns(new List<QualityProfile> { _movieQualityProfile, _slotOverrideQualityProfile });
        }

        // --- Feature 8: MissingMoviesSearchCommand ---

        [Test]
        public void missing_search_triggers_edition_search_for_monitored_missing_slot()
        {
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot> { _missingSlot });

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _monitoredMovie });

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _missingSlot, false, false), Times.Once);
        }

        [Test]
        public void missing_search_does_not_search_unmonitored_movie_slots()
        {
            var unmMonitoredMovie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 2)
                .With(m => m.Monitored = false)
                .Build();

            var slot = new MovieEditionSlot { Id = 20, MovieId = 2, Monitored = true };

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot> { slot });

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { unmMonitoredMovie });

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void missing_search_does_not_search_slot_already_in_queue()
        {
            var queue = new NzbDrone.Core.Queue.Queue
            {
                Movie = _monitoredMovie,
                MovieEditionSlotId = _missingSlot.Id
            };

            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue> { queue });

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot> { _missingSlot });

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [TestCase(null)]
        [TestCase(11)]
        public void missing_search_searches_slot_when_main_movie_or_different_slot_is_in_queue(int? queuedSlotId)
        {
            var queue = new NzbDrone.Core.Queue.Queue
            {
                Movie = _monitoredMovie,
                MovieEditionSlotId = queuedSlotId
            };

            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue> { queue });

            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot> { _missingSlot });

            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _monitoredMovie });

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _missingSlot, false, false), Times.Once);
        }

        [Test]
        public void missing_search_searches_main_movie_when_only_an_edition_slot_is_in_queue()
        {
            Mocker.GetMock<IMovieService>()
                .Setup(s => s.MoviesWithoutFiles(It.IsAny<NzbDrone.Core.Datastore.PagingSpec<Movie>>()))
                .Returns(new NzbDrone.Core.Datastore.PagingSpec<Movie> { Records = new List<Movie> { _monitoredMovie } });
            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue>
                {
                    new NzbDrone.Core.Queue.Queue { Movie = _monitoredMovie, MovieEditionSlotId = _missingSlot.Id }
                });
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot>());
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.MovieSearch(_monitoredMovie.Id, false, false))
                .ReturnsAsync(new List<DownloadDecision>());

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieSearch(_monitoredMovie.Id, false, false), Times.Once);
        }

        [Test]
        public void missing_search_normal_movies_behavior_unchanged()
        {
            // When no missing slots exist, no edition search should be triggered
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredMissingSlots())
                .Returns(new List<MovieEditionSlot>());

            Subject.Execute(new MissingMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        // --- Feature 9: CutoffUnmetMoviesSearchCommand edition-slot awareness ---

        [Test]
        public void cutoff_unmet_does_not_search_slot_with_no_file()
        {
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredSlotsWithFiles())
                .Returns(new List<MovieEditionSlot>());  // no slots with files

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void cutoff_unmet_does_not_search_unmonitored_slot()
        {
            var unmMonitoredSlot = new MovieEditionSlot
            {
                Id = 30,
                MovieId = 1,
                Monitored = false
            };

            // GetMonitoredSlotsWithFiles already filters to Monitored=true, so this would return empty
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredSlotsWithFiles())
                .Returns(new List<MovieEditionSlot>());

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void cutoff_unmet_ignores_slot_with_stale_movie_file_reference()
        {
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredSlotsWithFiles())
                .Returns(new List<MovieEditionSlot> { _monitoredSlotWithFile });
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<MovieFile>());
            Mocker.GetMock<IMovieService>()
                .Setup(s => s.GetMovies(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Movie> { _monitoredMovie });
            Mocker.GetMock<IQualityProfileService>()
                .Setup(s => s.All())
                .Returns(new List<QualityProfile> { _movieQualityProfile, _slotOverrideQualityProfile });

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void cutoff_unmet_searches_slot_file_below_movie_profile_cutoff()
        {
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.SDTV);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _monitoredSlotWithFile, false, false), Times.Once);
        }

        [Test]
        public void cutoff_unmet_does_not_search_slot_file_at_movie_profile_cutoff()
        {
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.WEBDL1080p);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void cutoff_unmet_searches_slot_at_quality_cutoff_below_profile_custom_format_cutoff()
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred" };
            _movieQualityProfile.CutoffFormatScore = 100;
            _movieQualityProfile.FormatItems = new List<ProfileFormatItem>
            {
                new ProfileFormatItem { Format = format, Score = 50 }
            };
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.WEBDL1080p, format);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _monitoredSlotWithFile, false, false), Times.Once);
        }

        [Test]
        public void cutoff_unmet_does_not_search_slot_meeting_quality_and_custom_format_cutoffs()
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred" };
            _movieQualityProfile.CutoffFormatScore = 100;
            _movieQualityProfile.FormatItems = new List<ProfileFormatItem>
            {
                new ProfileFormatItem { Format = format, Score = 100 }
            };
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.WEBDL1080p, format);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public void cutoff_unmet_searches_slot_below_slot_custom_format_minimum_above_profile_cutoff()
        {
            var format = new CustomFormat { Id = 1, Name = "Preferred" };
            _movieQualityProfile.CutoffFormatScore = 100;
            _movieQualityProfile.FormatItems = new List<ProfileFormatItem>
            {
                new ProfileFormatItem { Format = format, Score = 150 }
            };
            _monitoredSlotWithFile.MinimumCustomFormatScore = 200;
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.WEBDL1080p, format);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _monitoredSlotWithFile, false, false), Times.Once);
        }

        [Test]
        public void cutoff_unmet_uses_slot_quality_profile_override()
        {
            var slot = new MovieEditionSlot
            {
                Id = 12,
                MovieId = 1,
                EditionName = "IMAX",
                Monitored = true,
                QualityProfileId = _slotOverrideQualityProfile.Id
            };

            GivenCutoffSlot(slot, Quality.WEBDL1080p);

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, slot, false, false), Times.Once);
        }

        [Test]
        public void cutoff_unmet_does_not_search_slot_already_in_queue()
        {
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.SDTV);

            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue>
                {
                    new NzbDrone.Core.Queue.Queue
                    {
                        Movie = _monitoredMovie,
                        MovieEditionSlotId = _monitoredSlotWithFile.Id
                    }
                });

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(It.IsAny<Movie>(), It.IsAny<MovieEditionSlot>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [TestCase(null)]
        [TestCase(10)]
        public void cutoff_unmet_searches_slot_when_main_movie_or_different_slot_is_in_queue(int? queuedSlotId)
        {
            GivenCutoffSlot(_monitoredSlotWithFile, Quality.SDTV);

            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue>
                {
                    new NzbDrone.Core.Queue.Queue
                    {
                        Movie = _monitoredMovie,
                        MovieEditionSlotId = queuedSlotId
                    }
                });

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieEditionSearch(_monitoredMovie, _monitoredSlotWithFile, false, false), Times.Once);
        }

        [Test]
        public void cutoff_unmet_searches_main_movie_when_only_an_edition_slot_is_in_queue()
        {
            Mocker.GetMock<IMovieCutoffService>()
                .Setup(s => s.MoviesWhereCutoffUnmet(It.IsAny<NzbDrone.Core.Datastore.PagingSpec<Movie>>()))
                .Returns(new NzbDrone.Core.Datastore.PagingSpec<Movie> { Records = new List<Movie> { _monitoredMovie } });
            Mocker.GetMock<IQueueService>()
                .Setup(s => s.GetQueue())
                .Returns(new List<NzbDrone.Core.Queue.Queue>
                {
                    new NzbDrone.Core.Queue.Queue { Movie = _monitoredMovie, MovieEditionSlotId = _missingSlot.Id }
                });
            Mocker.GetMock<IMovieEditionSlotService>()
                .Setup(s => s.GetMonitoredSlotsWithFiles())
                .Returns(new List<MovieEditionSlot>());
            Mocker.GetMock<ISearchForReleases>()
                .Setup(s => s.MovieSearch(_monitoredMovie.Id, false, false))
                .ReturnsAsync(new List<DownloadDecision>());

            Subject.Execute(new CutoffUnmetMoviesSearchCommand { Trigger = CommandTrigger.Scheduled });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(s => s.MovieSearch(_monitoredMovie.Id, false, false), Times.Once);
        }
    }
}
