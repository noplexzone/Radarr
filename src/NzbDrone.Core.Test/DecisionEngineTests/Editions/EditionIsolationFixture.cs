using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Editions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.DecisionEngineTests.Editions
{
    // A release for one edition is never judged against another edition's file, queue item or monitoring
    [TestFixture]
    public class EditionIsolationFixture : CoreTest
    {
        private Movie _movie;
        private MovieEdition _default;
        private MovieEdition _finalCut;
        private RemoteMovie _finalCutRelease;

        [SetUp]
        public void Setup()
        {
            Mocker.Resolve<UpgradableSpecification>();
            CustomFormatsTestHelpers.GivenCustomFormats();

            _movie = Builder<Movie>.CreateNew()
                .With(m => m.Id = 1)
                .With(m => m.Monitored = true)
                .With(m => m.QualityProfile = new QualityProfile
                {
                    UpgradeAllowed = true,
                    Cutoff = Quality.Bluray1080p.Id,
                    Items = Qualities.QualityFixture.GetDefaultQualities(),
                    FormatItems = CustomFormatsTestHelpers.GetSampleFormatItems("None"),
                })
                .Build();

            // The default edition already has a file at cutoff
            _movie.MovieFileId = 1;
            _movie.MovieFile = new MovieFile { Id = 1, Quality = new QualityModel(Quality.Bluray1080p) };

            _default = MovieEdition.CreateDefault(_movie.Id);
            _default.Id = 1;

            _finalCut = MovieEdition.CreateNew(_movie.Id, "Final Cut", true);
            _finalCut.Id = 2;

            _finalCutRelease = GivenRelease(_finalCut, Quality.HDTV720p);

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(x => x.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>()))
                .Returns(new List<CustomFormat>());

            Mocker.GetMock<ICustomFormatCalculationService>()
                .Setup(x => x.ParseCustomFormat(It.IsAny<MovieFile>()))
                .Returns(new List<CustomFormat>());
        }

        private RemoteMovie GivenRelease(MovieEdition edition, Quality quality)
        {
            return new RemoteMovie
            {
                Movie = _movie,
                MovieEdition = edition,
                MovieEditionMatch = MovieEditionMatch.Matched(edition),
                ParsedMovieInfo = new ParsedMovieInfo { Quality = new QualityModel(quality) },
                CustomFormats = new List<CustomFormat>(),
                Release = new ReleaseInfo { Title = "Blade.Runner.1982" }
            };
        }

        private void GivenQueue(params RemoteMovie[] queued)
        {
            var queue = new List<Queue.Queue>();

            foreach (var remoteMovie in queued)
            {
                queue.Add(new Queue.Queue { RemoteMovie = remoteMovie, TrackedDownloadState = TrackedDownloadState.Downloading });
            }

            Mocker.GetMock<IQueueService>().Setup(s => s.GetQueue()).Returns(queue);
        }

        [Test]
        public void disk_check_should_ignore_other_editions_file()
        {
            Mocker.Resolve<UpgradeDiskSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
        }

        [Test]
        public void disk_check_should_compare_with_editions_own_file()
        {
            _finalCutRelease.EditionFile = new MovieFile { Id = 2, MovieEditionId = _finalCut.Id, Quality = new QualityModel(Quality.Bluray1080p) };

            Mocker.Resolve<UpgradeDiskSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation()).Accepted.Should().BeFalse();
        }

        [Test]
        public void disk_check_should_still_reject_default_edition_release_at_cutoff()
        {
            var release = GivenRelease(_default, Quality.HDTV720p);
            release.EditionFile = _movie.MovieFile;

            Mocker.Resolve<UpgradeDiskSpecification>().IsSatisfiedBy(release, new ReleaseDecisionInformation()).Accepted.Should().BeFalse();
        }

        [Test]
        public void queue_check_should_ignore_queued_other_edition()
        {
            GivenQueue(GivenRelease(_default, Quality.Bluray1080p));

            Mocker.Resolve<QueueSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
        }

        [Test]
        public void queue_check_should_reject_when_same_edition_is_queued()
        {
            GivenQueue(GivenRelease(_finalCut, Quality.Bluray1080p));

            Mocker.Resolve<QueueSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation()).Accepted.Should().BeFalse();
        }

        [Test]
        public void monitored_check_should_reject_unmonitored_edition()
        {
            _finalCut.Monitored = false;

            var decision = Mocker.Resolve<MonitoredMovieSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation());

            decision.Accepted.Should().BeFalse();
            decision.Reason.Should().Be(DownloadRejectionReason.EditionNotMonitored);
        }

        [Test]
        public void monitored_check_should_accept_monitored_edition_next_to_unmonitored_default()
        {
            _default.Monitored = false;

            Mocker.Resolve<MonitoredMovieSpecification>().IsSatisfiedBy(_finalCutRelease, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
        }
    }
}
