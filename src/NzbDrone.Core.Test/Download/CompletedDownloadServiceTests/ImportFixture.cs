using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download
{
    [TestFixture]
    public class ImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.1998")
                                                    .Build();

            var remoteMovie = BuildRemoteMovie();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteMovie = remoteMovie)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.MostRecentForDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new MovieHistory());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie("Drone.1998"))
                  .Returns(remoteMovie.Movie);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);
        }

        private RemoteMovie BuildRemoteMovie()
        {
            return new RemoteMovie
            {
                Movie = new Movie()
            };
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
               .Setup(s => s.MostRecentForDownloadId(It.Is<string>(i => i == "1234")))
               .Returns(new MovieHistory() { SourceTitle = "Droned 1998" });

            Mocker.GetMock<IParsingService>()
               .Setup(s => s.GetMovie(It.IsAny<string>()))
               .Returns((Movie)null);

            Mocker.GetMock<IParsingService>()
                .Setup(s => s.GetMovie("Droned 1998"))
                .Returns(BuildRemoteMovie().Movie);
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetMovie(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteMovie.Movie);
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_rejected()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1999.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent<DownloadCompletedEvent>(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            AssertNotImported();
        }

        [Test]
        public void ordinary_empty_import_should_remain_pending_without_manual_interaction()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                .Returns(new List<ImportResult>());

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            Mocker.GetMock<IEventAggregator>()
                .Verify(v => v.PublishEvent(It.IsAny<ManualInteractionRequiredEvent>()), Times.Never());
        }

        [Test]
        public void should_not_mark_as_imported_if_no_movies_were_parsed()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure"),

                               new ImportResult(
                                   new ImportDecision(
                                       new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }, new ImportRejection(ImportRejectionReason.Unknown, "Rejected!")), "Test Failure")
                           });

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_not_mark_as_imported_if_all_files_were_skipped()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure"),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv" }), "Test Failure")
                           });

            Subject.Import(_trackedDownload);

            AssertNotImported();
        }

        [Test]
        public void should_mark_as_imported_if_all_movies_were_imported_but_extra_files_were_not()
        {
            GivenSeriesMatch();

            _trackedDownload.RemoteMovie.Movie = new Movie();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie })),
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv" }), "Test Failure")
               });

            Subject.Import(_trackedDownload);

            AssertImported();
        }

        [Test]
        public void should_mark_as_imported_if_the_download_can_be_tracked_using_the_source_movieid()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
               {
                               new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.S01E01.mkv", Movie = _trackedDownload.RemoteMovie.Movie }))
               });

            Mocker.GetMock<IMovieService>()
                  .Setup(v => v.GetMovie(It.IsAny<int>()))
                  .Returns(BuildRemoteMovie().Movie);

            Subject.Import(_trackedDownload);

            AssertImported();
        }


        private TrackedDownload GroupedDownload(int movieId, MovieAcquisitionTarget target)
        {
            var item = new DownloadClientItem
            {
                DownloadId = "shared",
                Title = "Shared",
                OutputPath = new OsPath(@"C:\DropFolder\Shared".AsOsAgnostic())
            };
            return new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = item,
                MovieId = movieId,
                AcquisitionTarget = target,
                State = TrackedDownloadState.ImportPending,
                RemoteMovie = new RemoteMovie { Movie = new Movie { Id = movieId }, AcquisitionTarget = target }
            };
        }

        [Test]
        public void grouped_import_should_not_complete_from_a_wrong_target_result()
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            var wrong = new ImportResult(new ImportDecision(new LocalMovie
            {
                Path = @"C:\TestPath\Movie.mkv",
                Movie = slot.RemoteMovie.Movie,
                AcquisitionTarget = MovieAcquisitionTarget.Main
            }));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Returns(new List<PhysicalDownloadImportResult>
                {
                    new(main.Key, new List<ImportResult>()),
                    new(slot.Key, new List<ImportResult> { wrong })
                });

            Subject.ImportPhysicalGroup(new[] { main, slot });

            slot.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void grouped_import_should_complete_only_the_exact_successful_target()
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            var exact = new ImportResult(new ImportDecision(new LocalMovie
            {
                Path = @"C:\TestPath\Movie.mkv",
                Movie = slot.RemoteMovie.Movie,
                AcquisitionTarget = slot.AcquisitionTarget
            }));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Returns(new List<PhysicalDownloadImportResult>
                {
                    new(main.Key, new List<ImportResult>()),
                    new(slot.Key, new List<ImportResult> { exact })
                });

            Subject.ImportPhysicalGroup(new[] { main, slot });

            main.State.Should().Be(TrackedDownloadState.ImportBlocked);
            slot.State.Should().Be(TrackedDownloadState.Imported);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void grouped_import_should_use_only_exact_client_movie_target_download_history()
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            var wrongClient = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported, DownloadId = "shared", DownloadClientId = 2, MovieId = 10
            };
            MovieAcquisitionTargetSerializer.Write(wrongClient.Data, slot.AcquisitionTarget);
            var exact = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.FileImported, DownloadId = "shared", DownloadClientId = 1, MovieId = 10
            };
            MovieAcquisitionTargetSerializer.Write(exact.Data, slot.AcquisitionTarget);
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Returns(new[] { main, slot }.Select(download => new PhysicalDownloadImportResult(download.Key, new List<ImportResult>())).ToList());
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(service => service.GetLatestDownloadHistoryItemForTarget("shared", 1, 10, slot.AcquisitionTarget))
                .Returns(exact);

            Subject.ImportPhysicalGroup(new[] { main, slot });

            main.State.Should().Be(TrackedDownloadState.ImportBlocked);
            slot.State.Should().Be(TrackedDownloadState.Imported);
        }

        [Test]
        public void grouped_retry_should_process_only_pending_sibling_and_preserve_imported_sibling()
        {
            var imported = GroupedDownload(10, MovieAcquisitionTarget.Main);
            imported.State = TrackedDownloadState.Imported;
            var pending = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            IReadOnlyList<PhysicalDownloadImportEnvelope> captured = null;
            var exact = new ImportResult(new ImportDecision(new LocalMovie
            {
                Path = @"C:\TestPath\Movie.mkv",
                Movie = pending.RemoteMovie.Movie,
                AcquisitionTarget = pending.AcquisitionTarget
            }));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Callback<string, IReadOnlyList<PhysicalDownloadImportEnvelope>>((_, envelopes) => captured = envelopes)
                .Returns(new List<PhysicalDownloadImportResult>
                {
                    new(imported.Key, new List<ImportResult>()),
                    new(pending.Key, new List<ImportResult> { exact })
                });

            Subject.ImportPhysicalGroup(new[] { imported, pending });

            imported.State.Should().Be(TrackedDownloadState.Imported);
            pending.State.Should().Be(TrackedDownloadState.Imported);
            captured.Should().ContainSingle(envelope => envelope.Key == imported.Key && !envelope.ShouldImport);
            captured.Should().ContainSingle(envelope => envelope.Key == pending.Key && envelope.ShouldImport);
            Mocker.GetMock<IDownloadedMovieImportService>().Verify(service => service.ProcessPath(
                It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()), Times.Never());
        }

        [TestCase(DownloadHistoryEventType.DownloadGrabbed)]
        [TestCase(DownloadHistoryEventType.DownloadFailed)]
        [TestCase(DownloadHistoryEventType.DownloadIgnored)]
        public void grouped_completion_should_follow_newest_exact_lifecycle(DownloadHistoryEventType newestEvent)
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Returns(new[] { main, slot }.Select(download => new PhysicalDownloadImportResult(download.Key, new List<ImportResult>())).ToList());
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(service => service.GetLatestDownloadHistoryItemForTarget(
                    "shared", 1, 10, It.IsAny<MovieAcquisitionTarget>()))
                .Returns<string, int, int, MovieAcquisitionTarget>((_, _, _, target) =>
                    new DownloadHistory { EventType = newestEvent, DownloadId = "shared", DownloadClientId = 1, MovieId = 10 });

            Subject.ImportPhysicalGroup(new[] { main, slot });

            main.State.Should().Be(TrackedDownloadState.ImportBlocked);
            slot.State.Should().Be(TrackedDownloadState.ImportBlocked);
            Mocker.GetMock<IEventAggregator>().Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void grouped_manual_interaction_should_use_exact_download_history_without_movie_history()
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            var grab = new DownloadHistory
            {
                EventType = DownloadHistoryEventType.DownloadGrabbed,
                DownloadId = "shared",
                DownloadClientId = 1,
                MovieId = 10,
                SourceTitle = "Exact Grab"
            };
            MovieAcquisitionTargetSerializer.Write(grab.Data, main.AcquisitionTarget);
            Mocker.GetMock<IDownloadHistoryService>()
                .Setup(service => service.GetLatestGrabForTarget("shared", 1, 10, main.AcquisitionTarget))
                .Returns(grab);
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Returns(new[] { main, slot }.Select(download => new PhysicalDownloadImportResult(download.Key, new List<ImportResult>())).ToList());

            Subject.ImportPhysicalGroup(new[] { main, slot });

            Mocker.GetMock<IEventAggregator>().Verify(service => service.PublishEvent(
                It.Is<ManualInteractionRequiredEvent>(message => message.TrackedDownload == main && message.Release.Title == "Exact Grab")), Times.Once());
            Mocker.GetMock<IHistoryService>().Verify(service => service.FindByDownloadId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void grouped_import_should_restore_all_siblings_when_import_throws()
        {
            var main = GroupedDownload(10, MovieAcquisitionTarget.Main);
            var slot = GroupedDownload(10, MovieAcquisitionTarget.ForEditionSlot(42));
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Setup(service => service.ProcessPhysicalGroup(It.IsAny<string>(), It.IsAny<IReadOnlyList<PhysicalDownloadImportEnvelope>>()))
                .Throws(new System.InvalidOperationException("boom"));

            Assert.Throws<System.InvalidOperationException>(() => Subject.ImportPhysicalGroup(new[] { main, slot }));

            main.State.Should().Be(TrackedDownloadState.ImportBlocked);
            slot.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
        }

        private void AssertImported()
        {
            Mocker.GetMock<IDownloadedMovieImportService>()
                .Verify(v => v.ProcessPath(_trackedDownload.DownloadItem.OutputPath.FullPath, ImportMode.Auto, _trackedDownload.RemoteMovie.Movie, _trackedDownload.DownloadItem), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
        }
    }
}
