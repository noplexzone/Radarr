using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.QueueTests
{
    [TestFixture]
    public class QueueServiceFixture : CoreTest<QueueService>
    {
        private List<TrackedDownload> _trackedDownloads;

        [SetUp]
        public void SetUp()
        {
            var downloadClientInfo = Builder<DownloadClientItemClientInfo>.CreateNew().Build();

            var downloadItem = Builder<NzbDrone.Core.Download.DownloadClientItem>.CreateNew()
                                        .With(v => v.RemainingTime = TimeSpan.FromSeconds(10))
                                        .With(v => v.DownloadClientInfo = downloadClientInfo)
                                        .Build();

            var series = Builder<Movie>.CreateNew()
                                        .Build();

            var remoteEpisode = Builder<RemoteMovie>.CreateNew()
                                                   .With(r => r.Movie = series)
                                                   .With(r => r.ParsedMovieInfo = new ParsedMovieInfo())
                                                   .Build();

            _trackedDownloads = Builder<TrackedDownload>.CreateListOfSize(1)
                .All()
                .With(v => v.IsTrackable = true)
                .With(v => v.DownloadItem = downloadItem)
                .With(v => v.RemoteMovie = remoteEpisode)
                .With(v => v.MovieId = series.Id)
                .With(v => v.AcquisitionTarget = MovieAcquisitionTarget.Main)
                .Build()
                .ToList();
        }

        [Test]
        public void queue_should_preserve_stale_slot_target_when_remote_movie_is_unmapped()
        {
            _trackedDownloads[0].RemoteMovie = null;
            _trackedDownloads[0].MovieEditionSlotId = 42;

            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            Subject.GetQueue().Single().MovieEditionSlotId.Should().Be(42);
        }

        [Test]
        public void queue_ids_should_be_distinct_for_main_and_each_slot_of_same_download()
        {
            var original = _trackedDownloads.Single();
            original.DownloadClient = 7;
            original.DownloadItem.DownloadId = "shared";
            original.MovieId = 1;
            original.AcquisitionTarget = MovieAcquisitionTarget.Main;
            var slotA = Builder<TrackedDownload>.CreateNew()
                .With(v => v.IsTrackable = true)
                .With(v => v.DownloadClient = 7)
                .With(v => v.DownloadItem = original.DownloadItem)
                .With(v => v.RemoteMovie = (RemoteMovie)null)
                .With(v => v.MovieId = 1)
                .With(v => v.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42))
                .Build();
            var slotB = Builder<TrackedDownload>.CreateNew()
                .With(v => v.IsTrackable = true)
                .With(v => v.DownloadClient = 7)
                .With(v => v.DownloadItem = original.DownloadItem)
                .With(v => v.RemoteMovie = (RemoteMovie)null)
                .With(v => v.MovieId = 1)
                .With(v => v.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(43))
                .Build();

            Subject.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload> { original, slotA, slotB }));

            Subject.GetQueue().Should().HaveCount(3);
            Subject.GetQueue().Select(q => q.Id).Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void queue_items_should_have_id()
        {
            Subject.Handle(new TrackedDownloadRefreshedEvent(_trackedDownloads));

            var queue = Subject.GetQueue();

            queue.Should().HaveCount(1);

            queue.All(v => v.Id > 0).Should().BeTrue();

            var distinct = queue.Select(v => v.Id).Distinct().ToArray();

            distinct.Should().HaveCount(1);
        }
    }
}
