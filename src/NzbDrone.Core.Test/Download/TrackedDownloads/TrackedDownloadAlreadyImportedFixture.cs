using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.TrackedDownloads
{
    [TestFixture]
    public class TrackedDownloadAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private Movie _movie;
        private TrackedDownload _trackedDownload;
        private List<MovieHistory> _historyItems;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew().Build();

            var remoteMovie = Builder<RemoteMovie>.CreateNew()
                                                      .With(r => r.Movie = _movie)
                                                      .With(r => r.AcquisitionTarget = MovieAcquisitionTarget.Main)
                                                      .Build();

            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                         .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(t => t.RemoteMovie = remoteMovie)
                                                       .With(t => t.AcquisitionTarget = MovieAcquisitionTarget.Main)
                                                       .With(t => t.DownloadItem = downloadItem)
                                                       .Build();

            _historyItems = new List<MovieHistory>();
        }

        public void GivenHistoryForMovie(Movie movie, params MovieHistoryEventType[] eventTypes)
        {
            foreach (var eventType in eventTypes)
            {
                _historyItems.Add(
                    Builder<MovieHistory>.CreateNew()
                                            .With(h => h.MovieId = movie.Id)
                                            .With(h => h.EventType = eventType)
                                            .Build());
            }
        }

        [Test]
        public void should_return_false_if_there_is_no_history()
        {
            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_return_false_if_single_movie_download_is_not_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeFalse();
        }

        [Test]
        public void should_only_use_history_for_the_exact_target()
        {
            _trackedDownload.AcquisitionTarget = MovieAcquisitionTarget.ForEditionSlot(42);
            var otherSlotImport = Builder<MovieHistory>.CreateNew()
                .With(h => h.MovieId = _movie.Id)
                .With(h => h.EventType = MovieHistoryEventType.DownloadFolderImported)
                .Build();
            otherSlotImport.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "43");
            var exactGrab = Builder<MovieHistory>.CreateNew()
                .With(h => h.MovieId = _movie.Id)
                .With(h => h.EventType = MovieHistoryEventType.Grabbed)
                .Build();
            exactGrab.Data.Add(MovieHistory.MOVIE_EDITION_SLOT_ID, "42");
            _historyItems.Add(otherSlotImport);
            _historyItems.Add(exactGrab);

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeFalse();
        }

        [Test]
        public void should_return_true_if_single_movie_download_is_imported()
        {
            GivenHistoryForMovie(_movie, MovieHistoryEventType.DownloadFolderImported, MovieHistoryEventType.Grabbed);

            Subject.IsImported(_trackedDownload, _historyItems)
                   .Should()
                   .BeTrue();
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null, MovieAcquisitionTargetKind.Unknown, null)]
        [TestCase(MovieAcquisitionTargetKind.Unknown, null, MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 42, MovieAcquisitionTargetKind.EditionSlot, 43)]
        public void should_not_treat_a_different_target_import_as_imported(MovieAcquisitionTargetKind trackedKind, int? trackedSlotId, MovieAcquisitionTargetKind historyKind, int? historySlotId)
        {
            _trackedDownload.AcquisitionTarget = Target(trackedKind, trackedSlotId);
            var history = Builder<MovieHistory>.CreateNew()
                .With(h => h.MovieId = _movie.Id)
                .With(h => h.EventType = MovieHistoryEventType.DownloadFolderImported)
                .Build();
            MovieAcquisitionTargetSerializer.Write(history.Data, Target(historyKind, historySlotId));
            _historyItems.Add(history);

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeFalse();
        }

        [TestCase(MovieAcquisitionTargetKind.Main, null)]
        [TestCase(MovieAcquisitionTargetKind.Unknown, null)]
        [TestCase(MovieAcquisitionTargetKind.EditionSlot, 42)]
        public void should_match_import_for_the_same_explicit_target(MovieAcquisitionTargetKind kind, int? slotId)
        {
            var target = Target(kind, slotId);
            _trackedDownload.AcquisitionTarget = target;
            var history = Builder<MovieHistory>.CreateNew()
                .With(h => h.MovieId = _movie.Id)
                .With(h => h.EventType = MovieHistoryEventType.DownloadFolderImported)
                .Build();
            MovieAcquisitionTargetSerializer.Write(history.Data, target);
            _historyItems.Add(history);

            Subject.IsImported(_trackedDownload, _historyItems).Should().BeTrue();
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
