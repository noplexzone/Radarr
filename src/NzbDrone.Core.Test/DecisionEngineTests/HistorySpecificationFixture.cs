using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class HistorySpecificationFixture : CoreTest<HistorySpecification>
    {
        private const int FIRST_EPISODE_ID = 1;
        private const int SECOND_EPISODE_ID = 2;

        private HistorySpecification _upgradeHistory;

        private RemoteMovie _parseResultSingle;
        private QualityModel _upgradableQuality;
        private QualityModel _notupgradableQuality;
        private Movie _fakeMovie;

        [SetUp]
        public void Setup()
        {
            Mocker.Resolve<UpgradableSpecification>();
            _upgradeHistory = Mocker.Resolve<HistorySpecification>();

            CustomFormatsTestHelpers.GivenCustomFormats();

            _fakeMovie = Builder<Movie>.CreateNew()
                .With(c => c.QualityProfile = new QualityProfile
                {
                    Items = Qualities.QualityFixture.GetDefaultQualities(),
                    Cutoff = Quality.Bluray1080p.Id,
                    FormatItems = CustomFormatsTestHelpers.GetSampleFormatItems("None"),
                    MinFormatScore = 0,
                    UpgradeAllowed = true
                })
                .Build();

            _parseResultSingle = new RemoteMovie
            {
                Movie = _fakeMovie,
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(Quality.DVD, new Revision(version: 2)) },
                CustomFormats = new List<CustomFormat>()
            };

            _upgradableQuality = new QualityModel(Quality.SDTV, new Revision(version: 1));
            _notupgradableQuality = new QualityModel(Quality.HDTV1080p, new Revision(version: 2));

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(true);

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(x => x.ParseCustomFormat(It.IsAny<MovieHistory>(), It.IsAny<Movie>()))
                .Returns(new List<CustomFormat>());
        }

        private void GivenMostRecentForEpisode(int episodeId, string downloadId, QualityModel quality, DateTime date, MovieHistoryEventType eventType)
        {
            var history = new MovieHistory { DownloadId = downloadId, Quality = quality, Date = date, EventType = eventType };
            Mocker.GetMock<IHistoryService>().Setup(s => s.MostRecentForMovie(episodeId)).Returns(history);
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(episodeId, null)).Returns(new List<MovieHistory> { history });
        }

        private void GivenCdhDisabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(false);
        }

        [Test]
        public void should_return_true_if_it_is_a_search()
        {
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, new MovieSearchCriteria()).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_return_true_if_latest_history_item_is_null()
        {
            Mocker.GetMock<IHistoryService>().Setup(s => s.MostRecentForMovie(It.IsAny<int>())).Returns((MovieHistory)null);
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(It.IsAny<int>(), null)).Returns(new List<MovieHistory>());
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_return_true_if_latest_history_item_is_not_grabbed()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow, MovieHistoryEventType.DownloadFailed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        // [Test]
        //        public void should_return_true_if_latest_history_has_a_download_id_and_cdh_is_enabled()
        //        {
        //            GivenMostRecentForEpisode(FIRST_EPISODE_ID, "test", _notupgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
        //            _upgradeHistory.IsSatisfiedBy(_parseResultMulti, null).Accepted.Should().BeTrue();
        //        }
        [Test]
        public void should_return_true_if_latest_history_item_is_older_than_twelve_hours()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow.AddHours(-13), MovieHistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_be_upgradable_if_only_episode_is_upgradable()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, MovieHistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        /*
        [Test]
        public void should_be_upgradable_if_both_episodes_are_upgradable()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            GivenMostRecentForEpisode(SECOND_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultMulti, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_not_be_upgradable_if_both_episodes_are_not_upgradable()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            GivenMostRecentForEpisode(SECOND_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultMulti, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_be_not_upgradable_if_only_first_episodes_is_upgradable()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultMulti, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_be_not_upgradable_if_only_second_episodes_is_upgradable()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            GivenMostRecentForEpisode(SECOND_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, HistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultMulti, null).Accepted.Should().BeFalse();
        }*/

        [Test]
        public void should_not_be_upgradable_if_episode_is_of_same_quality_as_existing()
        {
            _fakeMovie.QualityProfile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.Bluray1080p.Id,
                FormatItems = CustomFormatsTestHelpers.GetSampleFormatItems(),
                MinFormatScore = 0
            };

            _parseResultSingle.ParsedMovieInfo.Quality = new QualityModel(Quality.WEBDL1080p, new Revision(version: 1));
            _upgradableQuality = new QualityModel(Quality.WEBDL1080p, new Revision(version: 1));

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(x => x.ParseCustomFormat(It.IsAny<MovieHistory>(), It.IsAny<Movie>()))
                .Returns(new List<CustomFormat>());

            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, MovieHistoryEventType.Grabbed);

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_not_be_upgradable_if_cutoff_already_met()
        {
            _fakeMovie.QualityProfile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.WEBDL1080p.Id,
                FormatItems = CustomFormatsTestHelpers.GetSampleFormatItems(),
                MinFormatScore = 0
            };

            _parseResultSingle.ParsedMovieInfo.Quality = new QualityModel(Quality.WEBDL1080p, new Revision(version: 1));
            _upgradableQuality = new QualityModel(Quality.Bluray1080p, new Revision(version: 1));

            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _upgradableQuality, DateTime.UtcNow, MovieHistoryEventType.Grabbed);

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_return_false_if_latest_history_item_is_only_one_hour_old()
        {
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, string.Empty, _notupgradableQuality, DateTime.UtcNow.AddHours(-1), MovieHistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_return_false_if_latest_history_has_a_download_id_and_cdh_is_disabled()
        {
            GivenCdhDisabled();
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, "test", _upgradableQuality, DateTime.UtcNow.AddDays(-100), MovieHistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_return_false_if_cutoff_already_met_and_cdh_is_disabled()
        {
            GivenCdhDisabled();
            _fakeMovie.QualityProfile = new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(),
                Cutoff = Quality.WEBDL1080p.Id,
                FormatItems = CustomFormatsTestHelpers.GetSampleFormatItems(),
                MinFormatScore = 0
            };

            _parseResultSingle.ParsedMovieInfo.Quality = new QualityModel(Quality.Bluray1080p, new Revision(version: 1));
            _upgradableQuality = new QualityModel(Quality.WEBDL1080p, new Revision(version: 1));

            GivenMostRecentForEpisode(FIRST_EPISODE_ID, "test", _upgradableQuality, DateTime.UtcNow.AddDays(-100), MovieHistoryEventType.Grabbed);

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_return_false_if_only_episode_is_not_upgradable_and_cdh_is_disabled()
        {
            GivenCdhDisabled();
            GivenMostRecentForEpisode(FIRST_EPISODE_ID, "test", _notupgradableQuality, DateTime.UtcNow.AddDays(-100), MovieHistoryEventType.Grabbed);
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_ignore_non_download_history_when_selecting_latest_main_target_event()
        {
            var grab = new MovieHistory { Date = DateTime.UtcNow.AddMinutes(-2), Quality = _notupgradableQuality, EventType = MovieHistoryEventType.Grabbed };
            var rename = new MovieHistory { Date = DateTime.UtcNow, Quality = _upgradableQuality, EventType = MovieHistoryEventType.MovieFileRenamed };
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(_fakeMovie.Id, null)).Returns(new List<MovieHistory> { grab, rename });

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        [TestCase(MovieHistoryEventType.DownloadFailed)]
        [TestCase(MovieHistoryEventType.DownloadIgnored)]
        [TestCase(MovieHistoryEventType.DownloadFolderImported)]
        public void should_use_latest_lifecycle_event_for_exact_edition_target(MovieHistoryEventType latestEventType)
        {
            _parseResultSingle.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42);
            var grab = new MovieHistory { Date = DateTime.UtcNow.AddMinutes(-2), Quality = _notupgradableQuality, EventType = MovieHistoryEventType.Grabbed };
            MovieAcquisitionTargetSerializer.Write(grab.Data, MovieAcquisitionTarget.ForEditionSlot(42));
            var latest = new MovieHistory { Date = DateTime.UtcNow.AddMinutes(-1), Quality = _notupgradableQuality, EventType = latestEventType };
            MovieAcquisitionTargetSerializer.Write(latest.Data, MovieAcquisitionTarget.ForEditionSlot(42));
            var other = new MovieHistory { Date = DateTime.UtcNow, Quality = _notupgradableQuality, EventType = MovieHistoryEventType.Grabbed };
            MovieAcquisitionTargetSerializer.Write(other.Data, MovieAcquisitionTarget.ForEditionSlot(43));
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(_fakeMovie.Id, null)).Returns(new List<MovieHistory> { grab, latest, other });
            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null, MovieAcquisitionTargetKind.Unknown, null)]
        [TestCase(MovieAcquisitionTargetKind.Unknown, null, MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 42, MovieAcquisitionTargetKind.EditionSlot, 43)]
        public void should_ignore_recent_grab_for_a_different_explicit_target(MovieAcquisitionTargetKind subjectKind, int? subjectSlotId, MovieAcquisitionTargetKind historyKind, int? historySlotId)
        {
            _parseResultSingle.AcquisitionTarget = Target(subjectKind, subjectSlotId);
            var history = new MovieHistory { Date = DateTime.UtcNow, Quality = _notupgradableQuality, EventType = MovieHistoryEventType.Grabbed };
            MovieAcquisitionTargetSerializer.Write(history.Data, Target(historyKind, historySlotId));
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(_fakeMovie.Id, null)).Returns(new List<MovieHistory> { history });

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeTrue();
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.Unknown, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 42)]
        public void should_use_recent_grab_for_the_same_explicit_target(MovieAcquisitionTargetKind kind, int? slotId)
        {
            var target = Target(kind, slotId);
            _parseResultSingle.AcquisitionTarget = target;
            var history = new MovieHistory { Date = DateTime.UtcNow, Quality = _notupgradableQuality, EventType = MovieHistoryEventType.Grabbed };
            MovieAcquisitionTargetSerializer.Write(history.Data, target);
            Mocker.GetMock<IHistoryService>().Setup(s => s.GetByMovieId(_fakeMovie.Id, null)).Returns(new List<MovieHistory> { history });

            _upgradeHistory.IsSatisfiedBy(_parseResultSingle, null).Accepted.Should().BeFalse();
        }

        private static MovieAcquisitionTarget Target(MovieAcquisitionTargetKind kind, int? slotId)
        {
            return kind == MovieAcquisitionTargetKind.Main
                ? MovieAcquisitionTarget.Main
                : kind == MovieAcquisitionTargetKind.EditionSlot
                    ? MovieAcquisitionTarget.ForEditionSlot(slotId.Value)
                    : MovieAcquisitionTarget.Unknown;
        }
    }
}
