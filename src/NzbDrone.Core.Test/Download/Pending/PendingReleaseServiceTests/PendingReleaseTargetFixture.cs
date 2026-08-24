using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class PendingReleaseTargetFixture : CoreTest<PendingReleaseService>
    {
        private Movie _movie;
        private List<PendingRelease> _pending;

        [SetUp]
        public void Setup()
        {
            _movie = new Movie
            {
                Id = 7,
                Tags = new HashSet<int>(),
                QualityProfile = new QualityProfile
                {
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Allowed = true, Quality = Quality.HDTV720p },
                        new QualityProfileQualityItem { Allowed = true, Quality = Quality.Bluray720p }
                    }
                }
            };
            _pending = new List<PendingRelease>();

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(v => v.AllByMovieId(_movie.Id))
                  .Returns(() => _pending.ToList());
            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(v => v.WithoutFallback())
                  .Returns(() => _pending.Where(p => p.Reason != PendingReleaseReason.Fallback).ToList());
            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovies(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Movie> { _movie });
            Mocker.GetMock<ITaskManager>()
                  .Setup(v => v.GetNextExecution(typeof(RssSyncCommand)))
                  .Returns(DateTime.UtcNow.AddMinutes(15));
            Mocker.GetMock<IDelayProfileService>()
                  .Setup(v => v.AllForTags(It.IsAny<HashSet<int>>()))
                  .Returns(new List<DelayProfile> { new DelayProfile { Order = 0 } });
            Mocker.GetMock<IDelayProfileService>()
                  .Setup(v => v.BestForTags(It.IsAny<HashSet<int>>()))
                  .Returns(new DelayProfile { Order = 0 });
        }

        private PendingRelease BuildPending(int id, MovieAcquisitionTarget target, Quality quality = null)
        {
            var info = new PendingReleaseAdditionalInfo
            {
                AcquisitionTargetKind = target.Kind,
                AcquisitionTargetEditionSlotId = target.EditionSlotId
            };

            return BuildPending(id, info, quality);
        }

        private PendingRelease BuildPending(int id, PendingReleaseAdditionalInfo info, Quality quality = null)
        {
            return new PendingRelease
            {
                Id = id,
                MovieId = _movie.Id,
                Title = $"A.Movie.2026.{id}",
                Added = DateTime.UtcNow,
                Reason = PendingReleaseReason.Delay,
                AdditionalInfo = info,
                ParsedMovieInfo = new ParsedMovieInfo
                {
                    MovieTitles = new List<string> { "A Movie" },
                    Quality = new QualityModel(quality ?? Quality.HDTV720p),
                    Languages = new List<Language> { Language.English }
                },
                Release = new ReleaseInfo
                {
                    Title = $"A.Movie.2026.{id}",
                    PublishDate = DateTime.UtcNow,
                    Size = 1000,
                    DownloadProtocol = DownloadProtocol.Usenet
                }
            };
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 41)]
        public void should_roundtrip_the_exact_persisted_target(MovieAcquisitionTargetKind kind, int? slotId)
        {
            var expected = kind == MovieAcquisitionTargetKind.EditionSlot
                ? MovieAcquisitionTarget.ForEditionSlot(slotId.Value)
                : MovieAcquisitionTarget.Main;
            _pending.Add(BuildPending(1, expected));

            Subject.GetPendingRemoteMovies(_movie.Id).Single().AcquisitionTarget.Should().Be(expected);
        }

        [Test]
        public void should_fail_closed_for_malformed_explicit_target_data()
        {
            _pending.Add(BuildPending(1, new PendingReleaseAdditionalInfo
            {
                AcquisitionTargetKind = MovieAcquisitionTargetKind.Main,
                AcquisitionTargetEditionSlotId = 41
            }));

            Subject.GetPendingRemoteMovies(_movie.Id).Single().AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Unknown);
        }

        [Test]
        public void should_map_only_legacy_absence_to_main()
        {
            _pending.Add(BuildPending(1, new PendingReleaseAdditionalInfo()));

            Subject.GetPendingRemoteMovies(_movie.Id).Single().AcquisitionTarget.Should().Be(MovieAcquisitionTarget.Main);
        }

        [Test]
        public void should_stamp_queue_items_and_select_each_target_independently()
        {
            var targets = new[]
            {
                MovieAcquisitionTarget.Main,
                MovieAcquisitionTarget.ForEditionSlot(41),
                MovieAcquisitionTarget.ForEditionSlot(42)
            };
            _pending.AddRange(targets.Select((target, index) => BuildPending(index + 1, target)));
            _pending.Add(BuildPending(4, MovieAcquisitionTarget.ForEditionSlot(41), Quality.Bluray720p));

            var queue = Subject.GetPendingQueue();

            queue.Should().HaveCount(3);
            queue.Select(q => q.AcquisitionTarget).Should().BeEquivalentTo(targets);
            queue.Single(q => q.AcquisitionTarget.Equals(MovieAcquisitionTarget.ForEditionSlot(41)))
                 .Quality.Quality.Should().Be(Quality.Bluray720p);
        }

        [Test]
        public void delayed_queue_action_should_receive_the_exact_reconstructed_slot_target()
        {
            var target = MovieAcquisitionTarget.ForEditionSlot(41);
            _pending.Add(BuildPending(1, target));
            var queueId = Subject.GetPendingQueue().Single().Id;

            var selected = Subject.FindPendingQueueItem(queueId);

            selected.AcquisitionTarget.Should().Be(target);
            selected.RemoteMovie.AcquisitionTarget.Should().Be(target);
        }
    }
}
